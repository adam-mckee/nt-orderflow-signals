namespace OrderFlowEngine.Modules;

public enum DivergenceType { None, Bullish, Bearish }

public enum OBType { Bullish, Bearish }

/// <summary>
/// An institutional order block zone.
/// A returned value with <c>Mitigated == true</c> means "not found."
/// </summary>
public struct OrderBlock
{
    public double High;
    public double Low;
    public OBType Type;
    public bool   Mitigated;
    public int    BarIndex;
}
