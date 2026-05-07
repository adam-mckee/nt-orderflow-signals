using System;
using System.Collections.Generic;

namespace NinjaTrader.NinjaScript.Indicators.OrderFlowSignals
{
    /// <summary>
    /// Builds a session volume-at-price profile and a rolling N-bar profile at tick-size
    /// granularity. Detects Low Volume Nodes (LVNs) — price gaps in the distribution — and
    /// exposes proximity checks used by the gatekeeper.
    ///
    /// Volume is distributed uniformly across each bar's High-Low tick range, which is a
    /// standard OHLCV approximation. Enable NinjaTrader Tick Replay for per-tick accuracy.
    ///
    /// POC / VAH / VAL follow the standard 70 % value-area convention (configurable).
    /// </summary>
    public sealed class VolumeProfileModule
    {
        // ── Parameters ────────────────────────────────────────────────────────────
        /// <summary>Fraction of total session volume that defines the Value Area (default 0.70).</summary>
        public double ValueAreaPercent    { get; set; } = 0.70;

        /// <summary>
        /// Volume fraction below which a price level is considered a Low Volume Node.
        /// A bucket is an LVN when its volume &lt; LVNThresholdPercent × max bucket volume.
        /// </summary>
        public double LVNThresholdPercent { get; set; } = 0.15;

        /// <summary>Number of completed bars kept in the rolling profile window.</summary>
        public int    RollingProfileBars  { get; set; } = 50;

        // ── Outputs ───────────────────────────────────────────────────────────────
        /// <summary>Point of Control: price level with the highest session volume.</summary>
        public double POC { get; private set; }
        /// <summary>Value Area High.</summary>
        public double VAH { get; private set; }
        /// <summary>Value Area Low.</summary>
        public double VAL { get; private set; }

        // Legacy Z-score outputs kept so the pre-existing gating code still compiles.
        /// <summary>True when the close bar's bucket is a statistical extreme (Z ≥ 1.5σ).</summary>
        public bool IsExtremeDetected { get; private set; }
        /// <summary>True = High Volume Node extreme; false = Low Volume Node extreme.</summary>
        public bool IsHVN             { get; private set; }

        // ── Internal state ────────────────────────────────────────────────────────
        private readonly double _tickSize;

        // Session profile: tick-index key → cumulative volume
        private readonly Dictionary<long, double> _session = new Dictionary<long, double>();

        // Rolling profile management
        private readonly Queue<Dictionary<long, double>>  _barHistory    = new Queue<Dictionary<long, double>>();
        private readonly Dictionary<long, double>          _rolling       = new Dictionary<long, double>();

        private bool _initialized;

        public VolumeProfileModule(double tickSize)
        {
            if (tickSize <= 0) throw new ArgumentException("tickSize must be positive.", nameof(tickSize));
            _tickSize = tickSize;
        }

        // ── Session lifecycle ─────────────────────────────────────────────────────

        /// <summary>Call at the RTH open (Bars.IsFirstBarOfSession) to reset the session profile.</summary>
        public void ResetSession(double openPrice)
        {
            _session.Clear();
            _initialized     = true;
            IsExtremeDetected = false;
            POC = VAH = VAL  = openPrice;
        }

        // ── Bar feed ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Feed a completed bar's OHLCV data. Volume is spread uniformly across all tick
        /// levels from Low to High.
        /// </summary>
        public void OnBarUpdate(double high, double low, double close, double volume)
        {
            if (!_initialized || volume <= 0 || high < low) return;

            long loKey = ToKey(low);
            long hiKey = ToKey(high);
            long span  = Math.Max(1, hiKey - loKey + 1);
            double perTick = volume / span;

            var barSlice = new Dictionary<long, double>((int)span);
            for (long k = loKey; k <= hiKey; k++)
            {
                barSlice[k] = perTick;
                AddTo(_session, k, perTick);
            }

            // Update rolling profile
            _barHistory.Enqueue(barSlice);
            foreach (var kvp in barSlice)
                AddTo(_rolling, kvp.Key, kvp.Value);

            while (_barHistory.Count > RollingProfileBars)
                SubtractSlice(_barHistory.Dequeue());

            RefreshStats(close);
        }

        // ── Gate interface ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns <c>true</c> if <paramref name="price"/> is within
        /// <paramref name="proximityTicks"/> ticks of any Low Volume Node in the
        /// <em>session</em> profile.
        /// </summary>
        public bool IsAtSessionExtreme(double price, double proximityTicks)
            => NearAnyLVN(_session, price, proximityTicks);

        /// <summary>
        /// Returns <c>true</c> if <paramref name="price"/> is within
        /// <paramref name="proximityTicks"/> ticks of any Low Volume Node in the
        /// rolling <see cref="RollingProfileBars"/>-bar profile.
        /// </summary>
        public bool IsAtRollingExtreme(double price, double proximityTicks)
            => NearAnyLVN(_rolling, price, proximityTicks);

        // ── Private helpers ───────────────────────────────────────────────────────

        private long   ToKey(double price) => (long)Math.Round(price / _tickSize);
        private double ToPrice(long key)   => key * _tickSize;

        private static void AddTo(Dictionary<long, double> dict, long key, double amount)
        {
            double cur;
            dict[key] = dict.TryGetValue(key, out cur) ? cur + amount : amount;
        }

        private void SubtractSlice(Dictionary<long, double> slice)
        {
            foreach (var kvp in slice)
            {
                double cur;
                if (!_rolling.TryGetValue(kvp.Key, out cur)) continue;
                cur -= kvp.Value;
                if (cur <= 0) _rolling.Remove(kvp.Key);
                else          _rolling[kvp.Key] = cur;
            }
        }

        private void RefreshStats(double close)
        {
            if (_session.Count == 0) return;

            // ── POC & totals ───────────────────────────────────────────────────────
            double total = 0;
            long   pocKey = 0;
            double pocVol = -1;

            foreach (var kvp in _session)
            {
                total += kvp.Value;
                if (kvp.Value > pocVol) { pocVol = kvp.Value; pocKey = kvp.Key; }
            }
            POC = ToPrice(pocKey);

            // ── Value Area ─────────────────────────────────────────────────────────
            var keys = new List<long>(_session.Keys);
            keys.Sort();

            int pocIdx = keys.BinarySearch(pocKey);
            if (pocIdx < 0) pocIdx = ~pocIdx;

            double target      = total * ValueAreaPercent;
            double accumulated = pocVol;
            int lo = pocIdx, hi = pocIdx;

            while (accumulated < target)
            {
                double above = (hi + 1 < keys.Count) ? _session[keys[hi + 1]] : 0;
                double below = (lo - 1 >= 0)         ? _session[keys[lo - 1]] : 0;
                if (above == 0 && below == 0) break;

                if (above >= below && hi + 1 < keys.Count)
                    accumulated += _session[keys[++hi]];
                else if (lo - 1 >= 0)
                    accumulated += _session[keys[--lo]];
                else
                    break;
            }
            VAH = ToPrice(keys[hi]);
            VAL = ToPrice(keys[lo]);

            // ── Legacy Z-score extreme (kept for backward compatibility) ───────────
            double mean   = total / _session.Count;
            double sumSq  = 0;
            foreach (var v in _session.Values) sumSq += (v - mean) * (v - mean);
            double sigma  = _session.Count > 1 ? Math.Sqrt(sumSq / (_session.Count - 1)) : 1;

            double closeVol;
            if (_session.TryGetValue(ToKey(close), out closeVol) && sigma > 0)
            {
                double z = (closeVol - mean) / sigma;
                IsExtremeDetected = Math.Abs(z) >= 1.5;
                IsHVN             = z > 0;
            }
            else
            {
                IsExtremeDetected = false;
            }
        }

        private bool NearAnyLVN(Dictionary<long, double> profile, double price, double proximityTicks)
        {
            if (profile.Count == 0) return false;

            double maxVol = 0;
            foreach (var v in profile.Values)
                if (v > maxVol) maxVol = v;

            double volThreshold   = maxVol * LVNThresholdPercent;
            double priceThreshold = proximityTicks * _tickSize;

            foreach (var kvp in profile)
            {
                if (kvp.Value > volThreshold) continue;
                if (Math.Abs(price - ToPrice(kvp.Key)) <= priceThreshold) return true;
            }
            return false;
        }
    }
}
