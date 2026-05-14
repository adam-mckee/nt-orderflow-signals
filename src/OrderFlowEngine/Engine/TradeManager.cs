using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlowEngine.BarBuilding;
using OrderFlowEngine.Config;
using OrderFlowEngine.Tradovate;

namespace OrderFlowEngine.Engine;

// ── Domain types ──────────────────────────────────────────────────────────────

public sealed class TradeRecord
{
    public Guid     Id          { get; } = Guid.NewGuid();
    public DateTime OpenTime    { get; init; }
    public string   Direction   { get; init; } = "";
    public double   EntryPrice  { get; init; }
    public double   StopPrice   { get; init; }
    public double   TargetPrice { get; init; }
    public int      Contracts   { get; init; }
    public double   RiskDollars { get; init; }
    public int?     TradovateOrderId { get; init; }
    public string   Signal      { get; init; } = "";  // trigger reason

    // Filled on close
    public DateTime? CloseTime  { get; set; }
    public double?   ExitPrice  { get; set; }
    public string?   ExitReason { get; set; }   // "Stop" | "Target" | "Flat"
    public double?   PnL        { get; set; }
    public bool      IsClosed   => CloseTime.HasValue;
}

// ── Manager ───────────────────────────────────────────────────────────────────

/// <summary>
/// Tracks the lifecycle of one position at a time.
/// On each primary bar close, checks whether the bar's High/Low crosses the
/// stop or target and closes the position accordingly.
///
/// P&amp;L is derived from bar price (simulation). For production, subscribe to
/// Tradovate fill/position events for exact fill prices.
/// </summary>
public sealed class TradeManager
{
    public TradeRecord?         OpenTrade   { get; private set; }
    public IReadOnlyList<TradeRecord> History => _history;

    public event Action<TradeRecord>? OnTradeOpened;
    public event Action<TradeRecord>? OnTradeClosed;

    private readonly List<TradeRecord> _history = new();
    private readonly double _tickSize;
    private readonly double _tickValue;
    private readonly ILogger<TradeManager> _log;

    public bool HasOpenPosition => OpenTrade != null;

    public TradeManager(IOptions<AppSettings> opts, ILogger<TradeManager> log)
    {
        _tickSize  = opts.Value.Signal.TickSize;
        _tickValue = opts.Value.Trading.TickValue;
        _log       = log;
    }

    // ── Open ──────────────────────────────────────────────────────────────────

    public void Open(TradeRecord trade)
    {
        OpenTrade = trade;
        _history.Add(trade);
        _log.LogInformation(
            "TRADE OPEN  {Dir} {Qty}x {Entry:F2} | stop={Stop:F2} target={Target:F2} risk=${Risk:F0}",
            trade.Direction, trade.Contracts, trade.EntryPrice,
            trade.StopPrice, trade.TargetPrice, trade.RiskDollars);
        OnTradeOpened?.Invoke(trade);
    }

    // ── Bar update (stop/target check) ────────────────────────────────────────

    public void OnBarClosed(Bar bar)
    {
        if (OpenTrade == null) return;

        bool isLong   = OpenTrade.Direction == "Long";
        bool stopHit   = isLong ? bar.Low  <= OpenTrade.StopPrice
                                : bar.High >= OpenTrade.StopPrice;
        bool targetHit = isLong ? bar.High >= OpenTrade.TargetPrice
                                : bar.Low  <= OpenTrade.TargetPrice;

        // Target takes priority if both cross in the same bar
        if (targetHit)
            Close(bar.CloseTime, OpenTrade.TargetPrice, "Target");
        else if (stopHit)
            Close(bar.CloseTime, OpenTrade.StopPrice,   "Stop");
    }

    // ── Close ─────────────────────────────────────────────────────────────────

    public void FlattenManually(DateTime time, double price)
        => Close(time, price, "Flat");

    private void Close(DateTime time, double exitPrice, string reason)
    {
        if (OpenTrade == null) return;

        bool isLong = OpenTrade.Direction == "Long";
        double priceDiff = isLong ? exitPrice - OpenTrade.EntryPrice
                                  : OpenTrade.EntryPrice - exitPrice;
        double pnl = priceDiff / _tickSize * _tickValue * OpenTrade.Contracts;

        OpenTrade.CloseTime  = time;
        OpenTrade.ExitPrice  = exitPrice;
        OpenTrade.ExitReason = reason;
        OpenTrade.PnL        = pnl;

        _log.LogInformation(
            "TRADE CLOSE [{Reason}]  exit={Exit:F2}  P&L={Pnl:+$0.00;-$0.00}",
            reason, exitPrice, pnl);

        var closed = OpenTrade;
        OpenTrade = null;
        OnTradeClosed?.Invoke(closed);
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    public (int Total, int Wins, double TotalPnL, double WinRate) DailyStats()
    {
        var today  = _history.Where(t => t.IsClosed && t.CloseTime?.Date == DateTime.Today).ToList();
        int wins   = today.Count(t => t.PnL > 0);
        double pnl = today.Sum(t => t.PnL ?? 0);
        double wr  = today.Count > 0 ? (double)wins / today.Count : 0;
        return (today.Count, wins, pnl, wr);
    }
}
