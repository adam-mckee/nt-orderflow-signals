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
/// tick feed. Signal logic and confluence rules are identical to the NT8 version.
/// </summary>
public sealed class SignalEngine : IAsyncDisposable
{
    private readonly SignalSettings   _cfg;
    private readonly string           _symbol;
    private readonly TradovateClient  _tradovate;
    private readonly NtfyClient       _ntfy;
    private readonly ILogger<SignalEngine> _log;

    // ── Bar aggregators ───────────────────────────────────────────────────────
    private readonly BarAggregator _agg3m;
    private readonly BarAggregator _agg1m;

    // ── Detection modules ─────────────────────────────────────────────────────
    private readonly VolumeProfileModule   _vp;
    private readonly CumulativeDeltaModule _cd3m;
    private readonly CumulativeDeltaModule _cd1m;
    private readonly OrderBlockModule      _ob;

    // ── Signal state ──────────────────────────────────────────────────────────
    private int _barCount3m;
    private int _lastSignalBar = int.MinValue;

    public SignalEngine(
        IOptions<AppSettings> opts,
        TradovateClient tradovate,
        NtfyClient ntfy,
        ILogger<SignalEngine> log,
        ILogger<BarAggregator> aggLog)
    {
        var settings = opts.Value;
        _cfg       = settings.Signal;
        _symbol    = settings.Tradovate.Symbol;
        _tradovate = tradovate;
        _ntfy      = ntfy;
        _log       = log;

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

        // Wire tick feed → both aggregators
        _tradovate.OnTick += OnTick;

        // Wire bar close events → module feeds
        _agg3m.OnBarClosed += On3mBarClosed;
        _agg1m.OnBarClosed += On1mBarClosed;
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

        // Warm-up guard
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

        EvaluateConfluence(bar);
    }

    // ── Confluence gating (identical to NT8 OrderFlowSignals.EvaluateConfluence) ─

    private void EvaluateConfluence(Bar bar)
    {
        if (_barCount3m - _lastSignalBar < _cfg.SignalCooldownBars) return;

        double price = bar.Close;
        int    prox  = _cfg.ObProximityTicks;

        bool atLVN = _vp.IsAtSessionExtreme(price, prox) || _vp.IsAtRollingExtreme(price, prox);
        DivergenceType div3m = _cd3m.GetDivergence(_cfg.DivergenceLookback);
        OrderBlock ob  = _ob.GetNearestValidOB(price, prox);
        bool hasOB     = !ob.Mitigated;
        DivergenceType div1m = _cd1m.GetDivergence(_cd1m.DivergenceLookback);

        _log.LogDebug("Gate check — LVN={L} div3m={D3} ob={OB} div1m={D1}",
            atLVN, div3m, hasOB ? ob.Type.ToString() : "none", div1m);

        bool longSignal = atLVN && div3m == DivergenceType.Bullish
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

        _ntfy.Send(_symbol, direction, price, reason);
    }

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
        return ValueTask.CompletedTask;
    }
}
