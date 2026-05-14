namespace NinjaTrader.NinjaScript.Indicators.OrderFlowSignals
{
    /// <summary>Direction of a cumulative-delta / price divergence.</summary>
    public enum DivergenceType { None, Bullish, Bearish }

    /// <summary>Whether the order block was formed before a bullish or bearish impulse.</summary>
    public enum OBType { Bullish, Bearish }

    /// <summary>
    /// An institutional order block zone.
    /// <see cref="Mitigated"/> is set to <c>true</c> when price trades through the zone,
    /// invalidating it. Treat a returned <c>Mitigated == true</c> value as "not found".
    /// </summary>
    public struct OrderBlock
    {
        /// <summary>Upper boundary of the zone.</summary>
        public double High;
        /// <summary>Lower boundary of the zone.</summary>
        public double Low;
        /// <summary>Whether this block was formed before a bullish or bearish impulse.</summary>
        public OBType Type;
        /// <summary>True when price has traded through the zone body, voiding it.</summary>
        public bool Mitigated;
        /// <summary>Absolute bar index when this block was identified.</summary>
        public int BarIndex;
    }
}
