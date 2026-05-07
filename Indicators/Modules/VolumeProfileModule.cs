using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators.OrderFlowSignals;

namespace NinjaTrader.NinjaScript.Indicators.OrderFlowSignals
{
    /// <summary>
    /// Builds a session volume-at-price histogram and detects statistical extremes.
    ///
    /// Gate logic: fires when the current bar's price is testing a High-Volume Node (HVN)
    /// or Low-Volume Node (LVN) that sits >= <see cref="ExtremeZScoreThreshold"/> standard
    /// deviations from the session mean volume distribution.
    ///
    /// POC / VAH / VAL follow standard TPO convention (70 % of volume).
    /// </summary>
    public sealed class VolumeProfileModule
    {
        // ── Parameters ────────────────────────────────────────────────────────────
        public int    TicksPerBucket          { get; set; } = 4;   // bucket granularity in ticks
        public double ExtremeZScoreThreshold  { get; set; } = 1.5; // σ cutoff for HVN/LVN
        public double ValueAreaPercent        { get; set; } = 0.70; // standard 70 %

        // ── Outputs (updated each bar) ────────────────────────────────────────────
        public double POC   { get; private set; }
        public double VAH   { get; private set; }
        public double VAL   { get; private set; }
        public bool   IsExtremeDetected { get; private set; }
        public bool   IsHVN            { get; private set; }  // true = HVN, false = LVN

        // ── Internal state ────────────────────────────────────────────────────────
        private readonly double _tickSize;
        private readonly Dictionary<long, double> _buckets = new(); // key = bucket index
        private double _sessionTotalVolume;
        private double _sessionStartPrice;
        private bool   _sessionInitialized;

        public VolumeProfileModule(double tickSize)
        {
            if (tickSize <= 0) throw new ArgumentException("tickSize must be positive.");
            _tickSize = tickSize;
        }

        // Call at the start of each new session / RTH open
        public void ResetSession(double openPrice)
        {
            _buckets.Clear();
            _sessionTotalVolume  = 0;
            _sessionStartPrice   = openPrice;
            _sessionInitialized  = true;
            IsExtremeDetected    = false;
        }

        /// <summary>
        /// Feed each completed bar's OHLCV data. Volume is apportioned linearly across
        /// the bar's price range to each bucket it spans.
        /// </summary>
        public void OnBarUpdate(double high, double low, double close, double volume)
        {
            if (!_sessionInitialized) return;

            long loIdx = PriceToBucket(low);
            long hiIdx = PriceToBucket(high);
            long span  = Math.Max(1, hiIdx - loIdx + 1);
            double perBucket = volume / span;

            for (long i = loIdx; i <= hiIdx; i++)
            {
                if (!_buckets.ContainsKey(i)) _buckets[i] = 0;
                _buckets[i] += perBucket;
            }
            _sessionTotalVolume += volume;

            RefreshStatistics(close);
        }

        private void RefreshStatistics(double currentClose)
        {
            if (_buckets.Count == 0) return;

            // ── POC ───────────────────────────────────────────────────────────────
            long pocIdx = _buckets.OrderByDescending(kv => kv.Value).First().Key;
            POC = BucketToPrice(pocIdx);

            // ── Value Area (70 %) ─────────────────────────────────────────────────
            double target = _sessionTotalVolume * ValueAreaPercent;
            double accumulated = _buckets.TryGetValue(pocIdx, out double pocVol) ? pocVol : 0;

            var sorted = _buckets.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
            long vahIdx = pocIdx;
            long valIdx = pocIdx;

            foreach (long idx in sorted.Skip(1))
            {
                if (accumulated >= target) break;
                accumulated += _buckets[idx];
                if (idx > vahIdx) vahIdx = idx;
                if (idx < valIdx) valIdx = idx;
            }
            VAH = BucketToPrice(vahIdx) + (_tickSize * TicksPerBucket);
            VAL = BucketToPrice(valIdx);

            // ── Z-score extreme detection ─────────────────────────────────────────
            double mean   = _sessionTotalVolume / _buckets.Count;
            double sumSq  = _buckets.Values.Sum(v => Math.Pow(v - mean, 2));
            double stdDev = _buckets.Count > 1 ? Math.Sqrt(sumSq / (_buckets.Count - 1)) : 1;

            long closeIdx = PriceToBucket(currentClose);
            if (_buckets.TryGetValue(closeIdx, out double closeVol) && stdDev > 0)
            {
                double z = (closeVol - mean) / stdDev;
                IsExtremeDetected = Math.Abs(z) >= ExtremeZScoreThreshold;
                IsHVN             = z > 0;
            }
            else
            {
                IsExtremeDetected = false;
            }
        }

        private long   PriceToBucket(double price) =>
            (long)Math.Floor(price / (_tickSize * TicksPerBucket));

        private double BucketToPrice(long idx) =>
            idx * _tickSize * TicksPerBucket;
    }
}
