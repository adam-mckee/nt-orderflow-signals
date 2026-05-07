using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.NinjaScript.Indicators.OrderFlowSignals;

namespace NinjaTrader.NinjaScript.Indicators.OrderFlowSignals
{
    /// <summary>
    /// Identifies institutional order blocks: the last opposite-colour candle before
    /// a strong impulsive move. These zones represent where large participants placed
    /// resting orders, making them high-probability reaction levels on a retest.
    ///
    /// Gate logic: fires when current price re-enters an active order block zone AND
    /// the zone has not been violated (no close through its body).
    ///
    /// Block identification follows Smart Money Concepts (SMC):
    ///   - Bullish OB: last bearish candle before a bullish impulse that breaks structure.
    ///   - Bearish OB: last bullish candle before a bearish impulse that breaks structure.
    /// </summary>
    public sealed class OrderBlockModule
    {
        // ── Parameters ────────────────────────────────────────────────────────────
        public int    ImpulseLookback      { get; set; } = 3;    // bars that must move after the OB
        public double MinImpulseMultiple   { get; set; } = 1.5;  // impulse ATR multiple to qualify
        public int    MaxActiveZones       { get; set; } = 6;
        public double TickSize             { get; set; } = 0.25;

        // ── Outputs ───────────────────────────────────────────────────────────────
        public bool              InActiveOrderBlock { get; private set; }
        public ZoneLevel?        CurrentZone        { get; private set; }
        public IReadOnlyList<ZoneLevel> ActiveZones => _activeZones;

        // ── Internal state ────────────────────────────────────────────────────────
        private readonly List<ZoneLevel>  _activeZones  = new();
        private readonly Queue<BarRecord> _barHistory   = new();

        private int    _barIndex;
        private double _atr; // simple rolling ATR approximation

        private record BarRecord(double Open, double High, double Low, double Close, int Index);

        /// <summary>
        /// Must be called on every bar. ATR should be the current 14-period ATR value,
        /// which the parent indicator passes in from its built-in ATR series.
        /// </summary>
        public void OnBarUpdate(double open, double high, double low, double close, double atr)
        {
            _atr      = atr;
            _barIndex++;

            _barHistory.Enqueue(new BarRecord(open, high, low, close, _barIndex));
            if (_barHistory.Count > ImpulseLookback + 2) _barHistory.Dequeue();

            InvalidateViolatedZones(close);
            TryIdentifyOrderBlock();
            CheckCurrentBarInZone(close);
            PruneExcessZones();
        }

        private void TryIdentifyOrderBlock()
        {
            var history = _barHistory.ToArray();
            if (history.Length < ImpulseLookback + 1) return;

            // Candidate OB is history[0], impulse is history[1..ImpulseLookback]
            var candidate = history[0];
            bool candidateBearish = candidate.Close < candidate.Open;
            bool candidateBullish = candidate.Close > candidate.Open;

            double impulseMove = 0;
            for (int i = 1; i < history.Length; i++)
                impulseMove += Math.Abs(history[i].Close - history[i - 1].Close);

            double threshold = _atr > 0 ? _atr * MinImpulseMultiple : TickSize * 8;
            if (impulseMove < threshold) return;

            double finalClose = history[^1].Close;
            bool bullishImpulse = finalClose > candidate.High;
            bool bearishImpulse = finalClose < candidate.Low;

            // Bearish OB: last bullish candle before bearish impulse
            if (candidateBullish && bearishImpulse)
            {
                AddZone(new ZoneLevel(
                    ZoneType.OrderBlock,
                    high:     candidate.High,
                    low:      candidate.Open,   // body low of bullish candle
                    bornBar:  candidate.Index,
                    bornTime: DateTime.UtcNow));
            }
            // Bullish OB: last bearish candle before bullish impulse
            else if (candidateBearish && bullishImpulse)
            {
                AddZone(new ZoneLevel(
                    ZoneType.OrderBlock,
                    high:     candidate.Open,   // body high of bearish candle
                    low:      candidate.Low,
                    bornBar:  candidate.Index,
                    bornTime: DateTime.UtcNow));
            }
        }

        private void CheckCurrentBarInZone(double close)
        {
            InActiveOrderBlock = false;
            CurrentZone        = null;

            foreach (var zone in _activeZones.Where(z => z.IsActive))
            {
                if (zone.Contains(close))
                {
                    zone.RegisterTouch();
                    InActiveOrderBlock = true;
                    CurrentZone        = zone;
                    break; // report the first matching zone; caller can iterate ActiveZones
                }
            }
        }

        private void InvalidateViolatedZones(double close)
        {
            foreach (var zone in _activeZones)
            {
                // A close through the full zone body voids it
                if (zone.IsActive && !zone.Contains(close))
                {
                    bool closedAbove = close > zone.High;
                    bool closedBelow = close < zone.Low;
                    // Only invalidate bearish OB if price closes above, bullish OB if below
                    if (closedAbove || closedBelow) zone.Invalidate();
                }
            }
        }

        private void AddZone(ZoneLevel zone)
        {
            // Deduplicate: skip if an active zone overlaps meaningfully
            bool overlapping = _activeZones.Any(z => z.IsActive &&
                z.High >= zone.Low && z.Low <= zone.High);
            if (!overlapping) _activeZones.Add(zone);
        }

        private void PruneExcessZones()
        {
            _activeZones.RemoveAll(z => !z.IsActive);
            while (_activeZones.Count > MaxActiveZones)
                _activeZones.RemoveAt(0); // drop oldest
        }
    }
}
