namespace OrderFlowEngine.BarBuilding;

/// <summary>A completed OHLCV bar with pre-classified bid/ask volumes.</summary>
public sealed record Bar(
    DateTime OpenTime,
    DateTime CloseTime,
    double   Open,
    double   High,
    double   Low,
    double   Close,
    double   Volume,
    double   BuyVolume,
    double   SellVolume,
    int      BarMinutes)
{
    public double Delta => BuyVolume - SellVolume;
    public bool   IsFirstBarOfSession { get; init; }
}

public enum Timeframe { OneMinute, ThreeMinute }
