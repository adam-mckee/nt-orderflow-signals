using System;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.Indicators.OrderFlowSignals
{
    public enum SignalDirection { Long, Short, Flat }

    public enum SignalStrength { Weak, Moderate, Strong }

    /// <summary>
    /// Immutable snapshot of a fully gated signal at a given bar index.
    /// </summary>
    public sealed class SignalResult
    {
        public SignalDirection Direction    { get; }
        public SignalStrength  Strength     { get; }
        public double          EntryPrice   { get; }
        public double          StopPrice    { get; }
        public double          TargetPrice  { get; }
        public int             BarIndex     { get; }
        public DateTime        Time         { get; }

        // Gate flags — all three must be true for Strength >= Moderate
        public bool VolumeExtremeConfirmed  { get; }
        public bool DeltaDivergenceConfirmed{ get; }
        public bool OrderBlockConfirmed     { get; }

        public int ConfluenceCount =>
            (VolumeExtremeConfirmed  ? 1 : 0) +
            (DeltaDivergenceConfirmed? 1 : 0) +
            (OrderBlockConfirmed     ? 1 : 0);

        public SignalResult(
            SignalDirection direction,
            double entryPrice, double stopPrice, double targetPrice,
            int barIndex, DateTime time,
            bool volumeExtreme, bool deltaDivergence, bool orderBlock)
        {
            Direction     = direction;
            EntryPrice    = entryPrice;
            StopPrice     = stopPrice;
            TargetPrice   = targetPrice;
            BarIndex      = barIndex;
            Time          = time;

            VolumeExtremeConfirmed   = volumeExtreme;
            DeltaDivergenceConfirmed = deltaDivergence;
            OrderBlockConfirmed      = orderBlock;

            Strength = ConfluenceCount switch
            {
                3 => SignalStrength.Strong,
                2 => SignalStrength.Moderate,
                _ => SignalStrength.Weak
            };
        }
    }
}
