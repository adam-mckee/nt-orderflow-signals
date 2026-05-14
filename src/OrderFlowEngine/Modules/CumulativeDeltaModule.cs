namespace OrderFlowEngine.Modules;

/// <summary>
/// Tracks cumulative delta (ask vol − bid vol) and detects price/delta divergences.
/// Identical logic to the NinjaTrader version; only the namespace changed.
/// </summary>
public sealed class CumulativeDeltaModule
{
    public int    DivergenceLookback { get; set; } = 5;
    public double MinDeltaThreshold  { get; set; } = 200;
    public bool   ResetOnSession     { get; set; } = true;

    public double CumulativeDelta { get; private set; }
    public double SessionDelta    { get; private set; }

    private DivergenceType _lastDiv = DivergenceType.None;
    private readonly List<double> _closes = new();
    private readonly List<double> _cdSnap  = new();
    private const int MaxHistory = 500;

    private double _barBuy;
    private double _barSell;
    private bool   _hasTickData;

    public void ResetSession()
    {
        if (ResetOnSession) SessionDelta = 0;
        _lastDiv = DivergenceType.None;
    }

    /// <summary>Classify a single trade tick for the bar currently forming.</summary>
    public void AddTick(double volume, bool isBuy)
    {
        if (isBuy) _barBuy  += volume;
        else       _barSell += volume;
        _hasTickData = true;
    }

    /// <summary>
    /// Call once per bar close. If AddTick was called this bar, tick data takes priority
    /// over the askVolume / bidVolume parameters.
    /// </summary>
    public void OnBarUpdate(double close, double askVolume, double bidVolume)
    {
        double barDelta;
        if (_hasTickData)
        {
            barDelta     = _barBuy - _barSell;
            _barBuy      = 0;
            _barSell     = 0;
            _hasTickData = false;
        }
        else
        {
            barDelta = askVolume - bidVolume;
        }

        CumulativeDelta += barDelta;
        SessionDelta    += barDelta;

        _closes.Add(close);
        _cdSnap.Add(CumulativeDelta);
        if (_closes.Count > MaxHistory) { _closes.RemoveAt(0); _cdSnap.RemoveAt(0); }

        _lastDiv = GetDivergence(DivergenceLookback);
    }

    /// <summary>
    /// Bearish: price HH but delta LH (exhaustion top).
    /// Bullish: price LL but delta HL (exhaustion bottom).
    /// </summary>
    public DivergenceType GetDivergence(int lookback)
    {
        int count = _closes.Count;
        if (count < lookback + 1) return DivergenceType.None;

        int endIdx   = count - 1;
        int startIdx = endIdx - lookback;

        double curClose = _closes[endIdx];
        double curCD    = _cdSnap[endIdx];

        double swingHighClose = double.MinValue, swingLowClose = double.MaxValue;
        double swingHighCD = 0, swingLowCD = 0;

        for (int i = startIdx; i < endIdx; i++)
        {
            double c = _closes[i], d = _cdSnap[i];
            if (c > swingHighClose) { swingHighClose = c; swingHighCD = d; }
            if (c < swingLowClose)  { swingLowClose  = c; swingLowCD  = d; }
        }

        bool sufficient = Math.Abs(curCD - swingHighCD) >= MinDeltaThreshold ||
                          Math.Abs(curCD - swingLowCD)  >= MinDeltaThreshold;
        if (!sufficient) return DivergenceType.None;

        if (curClose > swingHighClose && curCD < swingHighCD) return DivergenceType.Bearish;
        if (curClose < swingLowClose  && curCD > swingLowCD)  return DivergenceType.Bullish;
        return DivergenceType.None;
    }
}
