using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlowEngine.BarBuilding;
using OrderFlowEngine.Config;
using OrderFlowEngine.Modules;
using OrderFlowEngine.Tradovate;

namespace OrderFlowEngine.Engine;

/// <summary>
/// Headless port of the NinjaTrader <c>OrderFlowSignals</c> gatekeeper.
///
/// Wires three detection modules to bar streams from two <see cref="BarAggregator"/>
/// instances (3-min primary, 1-min confirmation) and a <see cref="TradovateClient"/>
/// tick feed. On confluence, optionally auto-executes a bracket order via
/// <see cref="TradovateOrderClient"/> and updates the <see cref="DashboardState"/>.
/// </summary>
public sealed class SignalEngine : IAsyncDisposable
{
    private readonly SignalSettings       _cfg;
    private readonly TradingSettings      _tradeCfg;
    private readonly string               _symbol;
    private readonly TradovateClient      _tradovate;
    private readonly TradovateOrderClient _orderClient;
    private readonly NtfyClient           _ntfy;
    private readonly PositionSizer        _sizer;
    private readonly TradeManager         _tradeManager;
    private readonly DashboardState       _dashboard;
    private readonly ILogger<SignalEngine> _log;

    // ── Bar aggregators ───────────────────────────────────────────────────────
    private readonly BarAggregator _agg3m;
    private readonly BarAggregator _agg1m;

    // ── Detection modules ─────────────────────────────────────────────────────
    private readonly VolumeProfileModule   _vp;
    private readonly CumulativeDeltaModule _cd3m;
    private readonly CumulativeDeltaModule _cd1m;
    private readonly OrderBlockModule      _ob;

    // ── State ─────────────────────────────────────────────────────────────────
    private int _barCount3m;
    private int _lastSignalBar = int.MinValue;
    private readonly CancellationTokenSource _cts = new();

    public SignalEngine(
        IOptions<AppSettings> opts,
        TradovateClient tradovate,
        TradovateOrderClient orderClient,
        NtfyClient ntfy,
        PositionSizer sizer,
        TradeManager tradeManager,
        DashboardState dashboard,
        ILogger<SignalEngine> log,
        ILogger<BarAggregator> aggLog)
    {
        var settings  = opts.Value;
        _cfg          = settings.Signal;
        _tradeCfg     = settings.Trading;
        _symbol       = settings.Tradovate.Symbol;
        _tradovate    = tradovate;
        _orderClient  = orderClient;
        _ntfy         = ntfy;
        _sizer        = sizer;
        _tradeManager = tradeManager;
        _dashboard    = dashboard;
        _log          = log;

        double ts = _cfg.TickSize;

        _agg3m = new BarAggregator(_cfg.PrimaryBarMinutes, _cfg.SessionStartHourET, aggLog);
        _agg1m = new BarAggregator(_cfg.ConfirmBarMinutes, _cfg.SessionStartHourET, aggLog);

        _vp = new VolumeProfileModule(ts)
        {
            ValueAreaPercent    = _cfg.ValueAreaPercent,
            LVNThresholdPercent = _cfg.LvnThresholdPercent,
            RollingProfileBars  = _cfg.RollingProfileBars,
        };
        _cd3m = new CumulativeDeltaModule { DivergenceLookback = _cfg.DivergenceLookback };
        _cd1m = new CumulativeDeltaModule { DivergenceLookback = _cfg.DivergenceLookback * 3 };
        _ob   = new OrderBlockModule
        {
            ImpulseLookback    = _cfg.ImpulseBars,
            MinImpulseMultiple = _cfg.ImpulseAtrMultiple,
            TickSize           = ts,
        };

        _tradovate.OnTick += OnTick;
        _agg3m.OnBarClosed += On3mBarClosed;
        _agg1m.OnBarClosed += On1mBarClosed;

        _tradeManager.OnTradeOpened += t => _dashboard.UpdateOpenTrade(t);
        _tradeManager.OnTradeClosed += t => _dashboard.RecordClosedTrade(t);
    }

    // ── Tick fan-out ──────────────────────────────────────────────────────────

    private void OnTick(Tick tick)
    {
        _agg3m.OnTick(tick);
        _agg1m.OnTick(tick);
    }

    // ── 1-min bar ─────────────────────────────────────────────────────────────

    private void On1mBarClosed(Bar bar, double atr)
    {
        double ask = bar.BuyVolume;
        double bid = bar.SellVolume;
        if (ask == 0 && bid == 0) { ask = bar.Volume / 2.0; bid = bar.Volume / 2.0; }
        _cd1m.OnBarUpdate(bar.Close, ask, bid);
    }

    // ── 3-min bar — main signal logic ─────────────────────────────────────────

    private void On3mBarClosed(Bar bar, double atr)
    {
        _barCount3m++;

        _tradeManager.OnBarClosed(bar);

        int minBars = Math.Max(_cfg.DivergenceLookback + 2, 16);
        if (_barCount3m < minBars) return;

        if (bar.IsFirstBarOfSession)
        {
            _vp.ResetSession(bar.Open);
            _cd3m.ResetSession();
            _log.LogInformation("New session — {Time:yyyy-MM-dd HH:mm} ET", bar.OpenTime);
        }

        _vp.OnBarUpdate(bar.High, bar.Low, bar.Close, bar.Volume);

        double ask = bar.BuyVolume;
        double bid = bar.SellVolume;
        if (ask == 0 && bid == 0) { ask = bar.Volume / 2.0; bid = bar.Volume / 2.0; }
        _cd3m.OnBarUpdate(bar.Close, ask, bid);

        _ob.OnBarUpdate(bar.Open, bar.High, bar.Low, bar.Close, atr);

        EvaluateConfluence(bar, atr);
    }

    // ── Confluence gating ─────────────────────────────────────────────────────

    private void EvaluateConfluence(Bar bar, double atr)
    {
        double price = bar.Close;
        int    prox  = _cfg.ObProximityTicks;

        bool atLVN = _vp.IsAtSessionExtreme(price, prox) || _vp.IsAtRollingExtreme(price, prox);
        DivergenceType div3m = _cd3m.GetDivergence(_cfg.DivergenceLookback);
        OrderBlock     ob    = _ob.GetNearestValidOB(price, prox);
        bool hasOB           = !ob.Mitigated;
        DivergenceType div1m = _cd1m.GetDivergence(_cd1m.DivergenceLookback);

        _dashboard.UpdateGates(
            atLVN,
            div3m.ToString(),
            div1m.ToString(),
            hasOB,
            hasOB ? ob.Type.ToString() : "—");

        if (_barCount3m - _lastSignalBar < _cfg.SignalCooldownBars) return;

        _log.LogDebug("Gate check — LVN={L} div3m={D3} ob={OB} div1m={D1}",
            atLVN, div3m, hasOB ? ob.Type.ToString() : "none", div1m);

        bool longSignal  = atLVN && div3m == DivergenceType.Bullish
                        && hasOB && ob.Type == OBType.Bullish
                        && div1m != DivergenceType.Bearish;

        bool shortSignal = atLVN && div3m == DivergenceType.Bearish
                        && hasOB && ob.Type == OBType.Bearish
                        && div1m != DivergenceType.Bullish;

        if (!longSignal && !shortSignal) return;

        string direction = longSignal ? "Long" : "Short";
        string reason    = BuildReason(atLVN, div3m, ob, div1m);
        _lastSignalBar   = _barCount3m;

        _log.LogInformation("*** {Dir} SIGNAL ***  {Time:HH:mm}  price={P:F2}  {R}",
            direction.ToUpperInvariant(), bar.CloseTime, price, reason);

        _ = ExecuteSignalAsync(direction, bar, atr, reason, _cts.Token);
    }

    // ── Signal execution (async: balance fetch + order placement) ─────────────

    private async Task ExecuteSignalAsync(
        string direction, Bar bar, double atr, string reason, CancellationToken ct)
    {
        try
        {
            double balance = _tradeCfg.Enabled
                ? await _orderClient.GetAccountBalanceAsync(ct)
                : 100_000.0;   // nominal for display when not trading

            var ps = _sizer.Calculate(direction, bar.Close, atr, balance);

            _log.LogInformation(
                "Position size: {Qty}x {Dir}  stop={Stop:F2}  target={Target:F2}  risk=${Risk:F0}",
                ps.Contracts, direction, ps.StopPrice, ps.TargetPrice, ps.RiskDollars);

            // Always record signal in feed and send alert
            _dashboard.RecordSignal(new SignalEntry(
                bar.CloseTime, direction, bar.Close,
                ps.Contracts, ps.StopPrice, ps.TargetPrice,
                ps.RiskDollars, reason));

            _ntfy.Send(_symbol, direction, bar.Close, reason,
                       ps.Contracts, ps.StopPrice, ps.TargetPrice);

            // Only open a trade record when no position is held
            if (_tradeManager.HasOpenPosition) return;

            int? orderId = null;
            if (_tradeCfg.Enabled)
            {
                orderId = await _orderClient.PlaceBracketOrderAsync(
                    direction, ps.Contracts, ps.StopPrice, ps.TargetPrice, ct);
            }

            var record = new TradeRecord
            {
                OpenTime         = bar.CloseTime,
                Direction        = direction,
                EntryPrice       = bar.Close,
                StopPrice        = ps.StopPrice,
                TargetPrice      = ps.TargetPrice,
                Contracts        = ps.Contracts,
                RiskDollars      = ps.RiskDollars,
                TradovateOrderId = orderId,
                Signal           = reason,
            };
            _tradeManager.Open(record);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "ExecuteSignalAsync failed");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildReason(bool atLVN, DivergenceType div3m,
                                      OrderBlock ob, DivergenceType div1m)
    {
        var parts = new List<string>(4);
        if (atLVN)                        parts.Add("LVN");
        if (div3m != DivergenceType.None) parts.Add(div3m + "Div3m");
        if (!ob.Mitigated)                parts.Add(ob.Type + "OB");
        if (div1m != DivergenceType.None) parts.Add(div1m + "Div1m");
        return string.Join("+", parts);
    }

    public ValueTask DisposeAsync()
    {
        _tradovate.OnTick -= OnTick;
        _cts.Cancel();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
