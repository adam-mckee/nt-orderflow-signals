namespace OrderFlowEngine.Modules;

/// <summary>
/// Builds a session volume-at-price profile and a rolling N-bar profile at tick-size
/// granularity. Detects Low Volume Nodes (LVNs) used as the first signal gate.
/// Identical logic to the NinjaTrader version; only the namespace changed.
/// </summary>
public sealed class VolumeProfileModule
{
    public double ValueAreaPercent    { get; set; } = 0.70;
    public double LVNThresholdPercent { get; set; } = 0.15;
    public int    RollingProfileBars  { get; set; } = 50;

    public double POC { get; private set; }
    public double VAH { get; private set; }
    public double VAL { get; private set; }

    private readonly double _tickSize;
    private readonly Dictionary<long, double> _session = new();
    private readonly Queue<Dictionary<long, double>> _barHistory = new();
    private readonly Dictionary<long, double> _rolling = new();
    private bool _initialized;

    public VolumeProfileModule(double tickSize)
    {
        if (tickSize <= 0) throw new ArgumentException("tickSize must be positive.", nameof(tickSize));
        _tickSize = tickSize;
    }

    public void ResetSession(double openPrice)
    {
        _session.Clear();
        _initialized   = true;
        POC = VAH = VAL = openPrice;
    }

    /// <summary>Feed a completed bar's OHLCV. Volume spread uniformly from Low to High.</summary>
    public void OnBarUpdate(double high, double low, double close, double volume)
    {
        if (!_initialized || volume <= 0 || high < low) return;

        long loKey = ToKey(low);
        long hiKey = ToKey(high);
        long span  = Math.Max(1, hiKey - loKey + 1);
        double perTick = volume / span;

        var slice = new Dictionary<long, double>((int)span);
        for (long k = loKey; k <= hiKey; k++)
        {
            slice[k] = perTick;
            AddTo(_session, k, perTick);
        }

        _barHistory.Enqueue(slice);
        foreach (var kvp in slice) AddTo(_rolling, kvp.Key, kvp.Value);

        while (_barHistory.Count > RollingProfileBars)
            SubtractSlice(_barHistory.Dequeue());

        RefreshStats(close);
    }

    /// <summary>True if price is within proximityTicks of any LVN in the session profile.</summary>
    public bool IsAtSessionExtreme(double price, double proximityTicks)
        => NearAnyLVN(_session, price, proximityTicks);

    /// <summary>True if price is within proximityTicks of any LVN in the rolling profile.</summary>
    public bool IsAtRollingExtreme(double price, double proximityTicks)
        => NearAnyLVN(_rolling, price, proximityTicks);

    private long   ToKey(double price) => (long)Math.Round(price / _tickSize);
    private double ToPrice(long key)   => key * _tickSize;

    private static void AddTo(Dictionary<long, double> d, long k, double v)
        => d[k] = d.TryGetValue(k, out double cur) ? cur + v : v;

    private void SubtractSlice(Dictionary<long, double> slice)
    {
        foreach (var kvp in slice)
        {
            if (!_rolling.TryGetValue(kvp.Key, out double cur)) continue;
            cur -= kvp.Value;
            if (cur <= 0) _rolling.Remove(kvp.Key);
            else          _rolling[kvp.Key] = cur;
        }
    }

    private void RefreshStats(double close)
    {
        if (_session.Count == 0) return;

        double total = 0;
        long pocKey = 0;
        double pocVol = -1;
        foreach (var kvp in _session)
        {
            total += kvp.Value;
            if (kvp.Value > pocVol) { pocVol = kvp.Value; pocKey = kvp.Key; }
        }
        POC = ToPrice(pocKey);

        var keys = new List<long>(_session.Keys);
        keys.Sort();
        int pocIdx = keys.BinarySearch(pocKey);
        if (pocIdx < 0) pocIdx = ~pocIdx;

        double target = total * ValueAreaPercent;
        double acc = pocVol;
        int lo = pocIdx, hi = pocIdx;
        while (acc < target)
        {
            double above = (hi + 1 < keys.Count) ? _session[keys[hi + 1]] : 0;
            double below = (lo - 1 >= 0)         ? _session[keys[lo - 1]] : 0;
            if (above == 0 && below == 0) break;
            if (above >= below && hi + 1 < keys.Count) acc += _session[keys[++hi]];
            else if (lo - 1 >= 0)                      acc += _session[keys[--lo]];
            else break;
        }
        VAH = ToPrice(keys[hi]);
        VAL = ToPrice(keys[lo]);
    }

    private bool NearAnyLVN(Dictionary<long, double> profile, double price, double proximityTicks)
    {
        if (profile.Count == 0) return false;
        double maxVol = 0;
        foreach (var v in profile.Values) if (v > maxVol) maxVol = v;
        double volThresh   = maxVol * LVNThresholdPercent;
        double priceThresh = proximityTicks * _tickSize;
        foreach (var kvp in profile)
            if (kvp.Value <= volThresh && Math.Abs(price - ToPrice(kvp.Key)) <= priceThresh)
                return true;
        return false;
    }
}
