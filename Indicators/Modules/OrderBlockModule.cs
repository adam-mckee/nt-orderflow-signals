using System;
using System.Collections.Generic;

namespace NinjaTrader.NinjaScript.Indicators.OrderFlowSignals
{
    /// <summary>
    /// Identifies bullish and bearish order blocks using Smart Money Concepts (SMC) logic
    /// and tracks their mitigation status across subsequent bars.
    ///
    /// Identification rules:
    ///   Bullish OB — the last <em>bearish</em> candle immediately before a bullish impulse
    ///                that moves ≥ <see cref="MinImpulseMultiple"/> × ATR(14).
    ///   Bearish OB — the last <em>bullish</em> candle immediately before a bearish impulse
    ///                that moves ≥ <see cref="MinImpulseMultiple"/> × ATR(14).
    ///
    /// Zone body convention (SMC standard):
    ///   Bullish OB zone: Open → Low  of the bearish candidate candle.
    ///   Bearish OB zone: High → Open of the bullish candidate candle.
    ///
    /// Mitigation: an OB is voided when the current bar's Low (bullish OB) or High (bearish OB)
    /// trades through the zone boundary. Only unmitigated OBs are returned by <see cref="GetNearestValidOB"/>.
    /// </summary>
    public sealed class OrderBlockModule
    {
        // ── Parameters ────────────────────────────────────────────────────────────
        /// <summary>Number of bars that must form the impulse leg after the candidate OB bar.</summary>
        public int    ImpulseLookback    { get; set; } = 3;

        /// <summary>
        /// The impulse High-Low range must exceed this multiple of ATR(14) to qualify.
        /// A fallback of TickSize × 8 is used when ATR is not yet available.
        /// </summary>
        public double MinImpulseMultiple { get; set; } = 1.5;

        /// <summary>Maximum number of live (unmitigated) order blocks retained simultaneously.</summary>
        public int    MaxActiveZones     { get; set; } = 8;

        /// <summary>Instrument tick size — required for proximity calculations.</summary>
        public double TickSize           { get; set; } = 0.25;

        // ── Outputs ───────────────────────────────────────────────────────────────
        /// <summary>All detected order blocks, including mitigated ones.</summary>
        public IReadOnlyList<OrderBlock> OrderBlocks => _obs;

        // Legacy property kept for backward compatibility with old gate logic.
        /// <summary>True when the current close is inside any active order block zone.</summary>
        public bool InActiveOrderBlock { get; private set; }

        // ── Internal state ────────────────────────────────────────────────────────
        private readonly List<OrderBlock> _obs        = new List<OrderBlock>();
        private readonly Queue<BarData>   _barHistory = new Queue<BarData>();
        private int    _barIndex;
        private double _atr;

        private struct BarData
        {
            public double Open, High, Low, Close;
            public int    Index;
        }

        // ── Bar feed ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Call on every bar close. <paramref name="atr"/> should be the ATR(14) value
        /// from the parent indicator's built-in ATR series.
        /// </summary>
        public void OnBarUpdate(double open, double high, double low, double close, double atr)
        {
            _atr = atr;
            _barIndex++;

            _barHistory.Enqueue(new BarData { Open = open, High = high, Low = low, Close = close, Index = _barIndex });

            // Keep enough history for OB scanning: ImpulseLookback impulse bars +
            // up to 5 bars before the impulse to find the last opposite-colour candle.
            int needed = ImpulseLookback + 6;
            while (_barHistory.Count > needed)
                _barHistory.Dequeue();

            UpdateMitigation(high, low);
            ScanForNewOrderBlock();
            UpdateLegacyFlag(close);
            Prune();
        }

        // ── Gate interface ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the nearest unmitigated <see cref="OrderBlock"/> whose zone midpoint is
        /// within <paramref name="proximityTicks"/> ticks of <paramref name="price"/>.
        /// Also matches when price is directly inside the zone body.
        /// Returns an <see cref="OrderBlock"/> with <c>Mitigated = true</c> when none is found
        /// (use <c>!ob.Mitigated</c> as the null check).
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

                // Qualify when inside the zone OR within range of either boundary.
                bool inZone    = price >= ob.Low && price <= ob.High;
                bool inProximity = dist <= range
                                || price >= ob.Low - range && price <= ob.High + range;

                if ((inZone || inProximity) && dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest     = ob;
                }
            }
            return nearest;
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private void UpdateMitigation(double high, double low)
        {
            for (int i = 0; i < _obs.Count; i++)
            {
                var ob = _obs[i];
                if (ob.Mitigated) continue;

                // Bullish OB voided when price low trades below zone low.
                // Bearish OB voided when price high trades above zone high.
                if ((ob.Type == OBType.Bullish && low  < ob.Low)  ||
                    (ob.Type == OBType.Bearish && high > ob.High))
                {
                    ob.Mitigated = true;
                    _obs[i]      = ob;
                }
            }
        }

        private void ScanForNewOrderBlock()
        {
            // history[0] = oldest bar, history[Length-1] = current bar (most recent)
            var history = _barHistory.ToArray();
            if (history.Length < ImpulseLookback + 1) return;

            double threshold = _atr > 0 ? _atr * MinImpulseMultiple : TickSize * 8;

            // The impulse window is the last ImpulseLookback bars (excluding current).
            // Candidate OB is the last opposite-colour candle just before the impulse.
            int impulseStart = history.Length - 1 - ImpulseLookback;
            int impulseEnd   = history.Length - 1;     // current bar

            if (impulseStart < 0) return;

            // Measure the impulse open-to-close direction and High-Low range.
            double impulseOpen  = history[impulseStart + 1].Open;
            double impulseClose = history[impulseEnd].Close;

            double impulseHigh = double.MinValue;
            double impulseLow  = double.MaxValue;
            for (int i = impulseStart + 1; i <= impulseEnd; i++)
            {
                if (history[i].High > impulseHigh) impulseHigh = history[i].High;
                if (history[i].Low  < impulseLow)  impulseLow  = history[i].Low;
            }

            double impulseRange = impulseHigh - impulseLow;
            if (impulseRange < threshold) return;

            bool bullishImpulse = impulseClose > impulseOpen;
            bool bearishImpulse = impulseClose < impulseOpen;
            if (!bullishImpulse && !bearishImpulse) return;

            // Walk back from impulseStart to find the last opposite-colour candidate.
            for (int i = impulseStart; i >= 0; i--)
            {
                var    c          = history[i];
                bool   isBullish  = c.Close > c.Open;
                bool   isBearish  = c.Close < c.Open;

                if (bullishImpulse && isBearish)
                {
                    // Last bearish candle before bullish impulse → Bullish OB
                    if (!IsTracked(c.Index))
                    {
                        _obs.Add(new OrderBlock
                        {
                            High      = c.Open,    // body high of bearish candle
                            Low       = c.Low,
                            Type      = OBType.Bullish,
                            Mitigated = false,
                            BarIndex  = c.Index,
                        });
                    }
                    break;
                }

                if (bearishImpulse && isBullish)
                {
                    // Last bullish candle before bearish impulse → Bearish OB
                    if (!IsTracked(c.Index))
                    {
                        _obs.Add(new OrderBlock
                        {
                            High      = c.High,
                            Low       = c.Open,    // body low of bullish candle
                            Type      = OBType.Bearish,
                            Mitigated = false,
                            BarIndex  = c.Index,
                        });
                    }
                    break;
                }
            }
        }

        private bool IsTracked(int barIndex)
        {
            foreach (var ob in _obs)
                if (ob.BarIndex == barIndex) return true;
            return false;
        }

        private void UpdateLegacyFlag(double close)
        {
            InActiveOrderBlock = false;
            foreach (var ob in _obs)
            {
                if (!ob.Mitigated && close >= ob.Low && close <= ob.High)
                {
                    InActiveOrderBlock = true;
                    break;
                }
            }
        }

        private void Prune()
        {
            // Remove mitigated entries once we exceed the cap.
            _obs.RemoveAll(ob => ob.Mitigated);
            while (_obs.Count > MaxActiveZones)
                _obs.RemoveAt(0);
        }
    }
}
