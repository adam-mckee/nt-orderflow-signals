namespace OrderFlowEngine.Engine;

/// <summary>
/// Singleton state bag consumed by Blazor Server components.
/// All writes come from signal/engine threads; components subscribe to
/// OnStateChanged and call InvokeAsync(StateHasChanged) from their context.
/// </summary>
public sealed class DashboardState
{
    // ── Gate state ────────────────────────────────────────────────────────────

    public bool   AtLVN          { get; private set; }
    public string Div3m          { get; private set; } = "None";
    public string Div1m          { get; private set; } = "None";
    public bool   HasOB          { get; private set; }
    public string OBType         { get; private set; } = "—";
    public DateTime LastGateUpdate { get; private set; }

    // ── Signal feed ───────────────────────────────────────────────────────────

    public IReadOnlyList<SignalEntry> RecentSignals => _signals;
    private readonly List<SignalEntry> _signals = new();
    private const int MaxSignals = 50;

    // ── Trade state ───────────────────────────────────────────────────────────

    public TradeRecord?                   OpenTrade  { get; private set; }
    public IReadOnlyList<TradeRecord> PnLHistory => _pnlHistory;
    private readonly List<TradeRecord> _pnlHistory = new();

    public event Action? OnStateChanged;

    // ── Mutators (called from engine threads) ─────────────────────────────────

    public void UpdateGates(bool atLvn, string div3m, string div1m,
                             bool hasOB, string obType)
    {
        AtLVN         = atLvn;
        Div3m         = div3m;
        Div1m         = div1m;
        HasOB         = hasOB;
        OBType        = obType;
        LastGateUpdate = DateTime.UtcNow;
        Notify();
    }

    public void RecordSignal(SignalEntry signal)
    {
        _signals.Insert(0, signal);
        if (_signals.Count > MaxSignals) _signals.RemoveAt(_signals.Count - 1);
        Notify();
    }

    public void UpdateOpenTrade(TradeRecord? trade)
    {
        OpenTrade = trade;
        Notify();
    }

    public void RecordClosedTrade(TradeRecord trade)
    {
        _pnlHistory.Insert(0, trade);
        OpenTrade = null;
        Notify();
    }

    private void Notify() => OnStateChanged?.Invoke();
}

public sealed record SignalEntry(
    DateTime Time,
    string   Direction,
    double   Price,
    int      Contracts,
    double   StopPrice,
    double   TargetPrice,
    double   RiskDollars,
    string   Reason);
