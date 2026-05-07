using System;
using System.Collections.Generic;

namespace NinjaTrader.NinjaScript.Indicators.OrderFlowSignals
{
    /// <summary>
    /// Tracks cumulative delta (ask volume − bid volume) on a per-bar basis and detects
    /// price / delta divergences using swing high-low comparison over a configurable
    /// lookback window.
    ///
    /// Divergence logic:
    ///   Bearish — price makes a higher high but delta makes a lower high (exhaustion top).
    ///   Bullish — price makes a lower low but delta makes a higher low (exhaustion bottom).
    ///
    /// Bid/ask volume source priority:
    ///   1. Tick-level feed via <see cref="AddTick"/> (most accurate — requires Tick Replay).
    ///   2. Pre-classified bar volumes passed to <see cref="OnBarUpdate"/>.
    ///   3. 50/50 split of bar volume (fallback when Tick Replay is off).
    /// </summary>
    public sealed class CumulativeDeltaModule
    {
        // ── Parameters ────────────────────────────────────────────────────────────
        /// <summary>Bars to scan when searching for swing high / low divergence.</summary>
        public int    DivergenceLookback { get; set; } = 5;

        /// <summary>
        /// Minimum absolute delta change between the current bar and the swing reference
        /// required to qualify as a divergence. Filters out low-volume noise.
        /// </summary>
        public double MinDeltaThreshold  { get; set; } = 200;

        /// <summary>When true, session delta resets to zero at the first bar of each session.</summary>
        public bool   ResetOnSession     { get; set; } = true;

        // ── Outputs ───────────────────────────────────────────────────────────────
        /// <summary>Running total of (ask volume − bid volume) since indicator start.</summary>
        public double CumulativeDelta { get; private set; }

        /// <summary>Running total of (ask volume − bid volume) since the last session reset.</summary>
        public double SessionDelta    { get; private set; }

        // Legacy bool properties for backward compatibility with existing gate checks.
        /// <summary>True when the last divergence check found a bearish divergence.</summary>
        public bool BearishDivergence  => _lastDiv == DivergenceType.Bearish;
        /// <summary>True when the last divergence check found a bullish divergence.</summary>
        public bool BullishDivergence  => _lastDiv == DivergenceType.Bullish;
        /// <summary>True when any divergence was found.</summary>
        public bool DivergenceDetected => _lastDiv != DivergenceType.None;

        // ── Internal state ────────────────────────────────────────────────────────
        private DivergenceType _lastDiv = DivergenceType.None;

        // Parallel time-series of bar closes and cumulative delta snapshots.
        // Index 0 = oldest, Count-1 = current bar.
        private readonly List<double> _closes = new List<double>();
        private readonly List<double> _cdSnap  = new List<double>(); // cumDelta after each bar
        private const int MaxHistory = 500;

        // Tick-level accumulator for the bar currently forming.
        private double _barBuy;
        private double _barSell;
        private bool   _hasTickData;

        // ── Session lifecycle ─────────────────────────────────────────────────────

        /// <summary>Call at session open (when <c>Bars.IsFirstBarOfSession</c> is true).</summary>
        public void ResetSession()
        {
            if (ResetOnSession) SessionDelta = 0;
            _lastDiv = DivergenceType.None;
        }

        // ── Tick-level feed ───────────────────────────────────────────────────────

        /// <summary>
        /// Add a single classified trade tick. Set <paramref name="isBuy"/> to <c>true</c>
        /// when the trade was an aggressor buy (price hit the ask).
        /// When called during a bar, this takes precedence over the <c>askVolume</c> /
        /// <c>bidVolume</c> parameters passed to <see cref="OnBarUpdate"/>.
        /// </summary>
        public void AddTick(double volume, bool isBuy)
        {
            if (isBuy) _barBuy  += volume;
            else       _barSell += volume;
            _hasTickData = true;
        }

        // ── Bar feed ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Call once per bar close with the bar's close price and aggregated bid/ask volumes.
        /// If <see cref="AddTick"/> was called during this bar, tick accumulation takes
        /// precedence over <paramref name="askVolume"/> / <paramref name="bidVolume"/>.
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

            if (_closes.Count > MaxHistory)
            {
                _closes.RemoveAt(0);
                _cdSnap.RemoveAt(0);
            }

            _lastDiv = GetDivergence(DivergenceLookback);
        }

        // ── Gate interface ────────────────────────────────────────────────────────

        /// <summary>
        /// Checks for a price / delta divergence over the last <paramref name="lookback"/> bars.
        ///
        /// The algorithm compares the <em>current</em> bar against the highest high and lowest
        /// low of the preceding <paramref name="lookback"/> bars:
        /// <list type="bullet">
        ///   <item>Bearish: current close &gt; prior swing high AND current delta &lt; delta at that high.</item>
        ///   <item>Bullish: current close &lt; prior swing low AND current delta &gt; delta at that low.</item>
        /// </list>
        /// </summary>
        public DivergenceType GetDivergence(int lookback)
        {
            int count = _closes.Count;
            if (count < lookback + 1) return DivergenceType.None;

            int endIdx   = count - 1;           // current bar
            int startIdx = endIdx - lookback;   // oldest bar in the window

            double curClose = _closes[endIdx];
            double curCD    = _cdSnap[endIdx];

            // Find the swing high and swing low over the prior bars (excluding current).
            double swingHighClose = double.MinValue;
            double swingLowClose  = double.MaxValue;
            double swingHighCD    = 0;
            double swingLowCD     = 0;

            for (int i = startIdx; i < endIdx; i++)
            {
                double c = _closes[i];
                double d = _cdSnap[i];

                if (c > swingHighClose) { swingHighClose = c; swingHighCD = d; }
                if (c < swingLowClose)  { swingLowClose  = c; swingLowCD  = d; }
            }

            // Require minimum delta move to avoid noise on low-volume bars.
            bool deltaMoveSufficient =
                Math.Abs(curCD - swingHighCD) >= MinDeltaThreshold ||
                Math.Abs(curCD - swingLowCD)  >= MinDeltaThreshold;

            if (!deltaMoveSufficient) return DivergenceType.None;

            // Bearish divergence: price HH but delta LH
            if (curClose > swingHighClose && curCD < swingHighCD)
                return DivergenceType.Bearish;

            // Bullish divergence: price LL but delta HL
            if (curClose < swingLowClose && curCD > swingLowCD)
                return DivergenceType.Bullish;

            return DivergenceType.None;
        }
    }
}
