using System;

namespace NinjaTrader.NinjaScript.Indicators.OrderFlowSignals
{
    public enum ZoneType { OrderBlock, ImbalanceZone, VolumeNode }

    /// <summary>
    /// A price zone persisted across bars for confluence tracking.
    /// </summary>
    public sealed class ZoneLevel
    {
        public ZoneType  Type       { get; }
        public double    High       { get; }
        public double    Low        { get; }
        public double    MidPoint   => (High + Low) / 2.0;
        public int       BornBar    { get; }
        public DateTime  BornTime   { get; }
        public bool      IsActive   { get; private set; } = true;
        public int       TouchCount { get; private set; }

        public ZoneLevel(ZoneType type, double high, double low, int bornBar, DateTime bornTime)
        {
            if (high <= low) throw new ArgumentException("Zone high must exceed low.");
            Type     = type;
            High     = high;
            Low      = low;
            BornBar  = bornBar;
            BornTime = bornTime;
        }

        public bool Contains(double price) => price >= Low && price <= High;

        public void RegisterTouch() => TouchCount++;

        // Invalidate when price closes through the zone body
        public void Invalidate() => IsActive = false;
    }
}
