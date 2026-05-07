using System;
using System.Collections.Generic;
using NinjaTrader.NinjaScript.Indicators.OrderFlowSignals;

namespace NinjaTrader.NinjaScript.Indicators.OrderFlowSignals
{
    /// <summary>
    /// Tracks cumulative delta (ask volume minus bid volume) and identifies
    /// delta divergences against price for mean-reversion setups.
    ///
    /// Gate logic: a divergence fires when price makes a higher high (or lower low)
    /// but cumulative delta makes the opposite extreme over the same lookback window,
    /// indicating that the price move is not supported by real directional order flow.
    ///
    /// Requires per-bar bid/ask volume — feed from a MarketDepth or BarType that
    /// captures bid/ask (e.g. NinjaTrader Tick Replay or Level II data).
    /// </summary>
    public sealed class CumulativeDeltaModule
    {
        // ── Parameters ────────────────────────────────────────────────────────────
        public int    DivergenceLookback  { get; set; } = 10;   // bars to scan for swing extremes
        public double MinDeltaThreshold   { get; set; } = 200;  // minimum absolute delta to qualify
        public bool   ResetOnSession      { get; set; } = true;

        // ── Outputs ───────────────────────────────────────────────────────────────
        public double CumulativeDelta      { get; private set; }
        public double SessionDelta         { get; private set; }
        public bool   BearishDivergence    { get; private set; } // price up, delta down
        public bool   BullishDivergence    { get; private set; } // price down, delta up
        public bool   DivergenceDetected   => BearishDivergence || BullishDivergence;

        // ── Rolling windows ───────────────────────────────────────────────────────
        private readonly Queue<double> _priceWindow = new();
        private readonly Queue<double> _deltaWindow = new();

        public void ResetSession()
        {
            if (ResetOnSession) SessionDelta = 0;
        }

        /// <summary>
        /// Feed per-bar aggregated bid and ask volumes.
        /// In NinjaTrader, use <c>GetCurrentBidVolume()</c> / <c>GetCurrentAskVolume()</c>
        /// inside <c>OnBarUpdate</c> when <c>Calculate == Calculate.OnEachTick</c>.
        /// </summary>
        public void OnBarUpdate(double close, double askVolume, double bidVolume)
        {
            double barDelta = askVolume - bidVolume;
            CumulativeDelta += barDelta;
            SessionDelta    += barDelta;

            EnqueueRolling(_priceWindow, close,             DivergenceLookback);
            EnqueueRolling(_deltaWindow, CumulativeDelta,   DivergenceLookback);

            DetectDivergence();
        }

        private void DetectDivergence()
        {
            BearishDivergence = false;
            BullishDivergence = false;

            if (_priceWindow.Count < DivergenceLookback) return;

            double[] prices = [.. _priceWindow];
            double[] deltas = [.. _deltaWindow];

            double firstPrice = prices[0];
            double lastPrice  = prices[^1];
            double firstDelta = deltas[0];
            double lastDelta  = deltas[^1];

            // Require minimum absolute delta magnitude to filter noise
            if (Math.Abs(lastDelta - firstDelta) < MinDeltaThreshold) return;

            bool priceUp   = lastPrice > firstPrice;
            bool priceDown = lastPrice < firstPrice;
            bool deltaUp   = lastDelta > firstDelta;
            bool deltaDown = lastDelta < firstDelta;

            BearishDivergence = priceUp   && deltaDown; // exhaustion top
            BullishDivergence = priceDown && deltaUp;   // exhaustion bottom
        }

        private static void EnqueueRolling(Queue<double> queue, double value, int maxLength)
        {
            queue.Enqueue(value);
            if (queue.Count > maxLength) queue.Dequeue();
        }
    }
}
