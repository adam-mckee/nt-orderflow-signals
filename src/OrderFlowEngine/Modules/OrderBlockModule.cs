namespace OrderFlowEngine.Modules;

/// <summary>
/// Identifies SMC order blocks and tracks mitigation.
/// Identical logic to the NinjaTrader version; only the namespace changed.
/// </summary>
public sealed class OrderBlockModule
{
    public int    ImpulseLookback    { get; set; } = 3;
    public double MinImpulseMultiple { get; set; } = 1.5;
    public int    MaxActiveZones     { get; set; } = 8;
    public double TickSize           { get; set; } = 0.25;

    public IReadOnlyList<OrderBlock> OrderBlocks => _obs;
    public bool InActiveOrderBlock { get; private set; }

    private readonly List<OrderBlock> _obs        = new();
    private readonly Queue<BarData>   _barHistory = new();
    private int    _barIndex;
    private double _atr;

    private struct BarData { public double Open, High, Low, Close; public int Index; }

    public void OnBarUpdate(double open, double high, double low, double close, double atr)
    {
        _atr = atr;
        _barIndex++;
        _barHistory.Enqueue(new BarData { Open = open, High = high, Low = low, Close = close, Index = _barIndex });
        while (_barHistory.Count > ImpulseLookback + 6) _barHistory.Dequeue();

        UpdateMitigation(high, low);
        ScanForNewOrderBlock();
        UpdateLegacyFlag(close);
        Prune();
    }

    /// <summary>
    /// Returns the nearest unmitigated OB within proximityTicks.
    /// Returns an OrderBlock with Mitigated=true when none found.
    /// </summary>
    public OrderBlock GetNearestValidOB(double price, double proximityTicks)
    {
        double range   = proximityTicks * TickSize;
        var    nearest = new OrderBlock { Mitigated = true };
        double nearestDist = double.MaxValue;

        foreach (var ob in _obs)
        {
            if (ob.Mitigated) continue;
            double mid  = (ob.High + ob.Low) / 2.0;
            double dist = Math.Abs(price - mid);
            bool inRange = dist <= range || (price >= ob.Low - range && price <= ob.High + range);
            if (inRange && dist < nearestDist) { nearestDist = dist; nearest = ob; }
        }
        return nearest;
    }

    private void UpdateMitigation(double high, double low)
    {
        for (int i = 0; i < _obs.Count; i++)
        {
            var ob = _obs[i];
            if (ob.Mitigated) continue;
            if ((ob.Type == OBType.Bullish && low  < ob.Low) ||
                (ob.Type == OBType.Bearish && high > ob.High))
            { ob.Mitigated = true; _obs[i] = ob; }
        }
    }

    private void ScanForNewOrderBlock()
    {
        var history = _barHistory.ToArray();
        if (history.Length < ImpulseLookback + 1) return;

        double threshold = _atr > 0 ? _atr * MinImpulseMultiple : TickSize * 8;
        int impulseStart = history.Length - 1 - ImpulseLookback;
        if (impulseStart < 0) return;

        double impulseOpen  = history[impulseStart + 1].Open;
        double impulseClose = history[^1].Close;
        double impulseHigh  = double.MinValue, impulseLow = double.MaxValue;
        for (int i = impulseStart + 1; i < history.Length; i++)
        {
            if (history[i].High > impulseHigh) impulseHigh = history[i].High;
            if (history[i].Low  < impulseLow)  impulseLow  = history[i].Low;
        }
        if (impulseHigh - impulseLow < threshold) return;

        bool bullish = impulseClose > impulseOpen;
        bool bearish = impulseClose < impulseOpen;
        if (!bullish && !bearish) return;

        for (int i = impulseStart; i >= 0; i--)
        {
            var c = history[i];
            if (bullish && c.Close < c.Open && !IsTracked(c.Index))
            {
                _obs.Add(new OrderBlock { High = c.Open, Low = c.Low, Type = OBType.Bullish, BarIndex = c.Index });
                break;
            }
            if (bearish && c.Close > c.Open && !IsTracked(c.Index))
            {
                _obs.Add(new OrderBlock { High = c.High, Low = c.Open, Type = OBType.Bearish, BarIndex = c.Index });
                break;
            }
        }
    }

    private bool IsTracked(int idx) { foreach (var ob in _obs) if (ob.BarIndex == idx) return true; return false; }

    private void UpdateLegacyFlag(double close)
    {
        InActiveOrderBlock = false;
        foreach (var ob in _obs)
            if (!ob.Mitigated && close >= ob.Low && close <= ob.High) { InActiveOrderBlock = true; break; }
    }

    private void Prune()
    {
        _obs.RemoveAll(ob => ob.Mitigated);
        while (_obs.Count > MaxActiveZones) _obs.RemoveAt(0);
    }
}
