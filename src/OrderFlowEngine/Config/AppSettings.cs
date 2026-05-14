namespace OrderFlowEngine.Config;

public sealed class AppSettings
{
    public TradovateSettings Tradovate { get; set; } = new();
    public SignalSettings    Signal    { get; set; } = new();
    public TradingSettings   Trading   { get; set; } = new();
    public AlertSettings     Alerts    { get; set; } = new();
}

public sealed class TradovateSettings
{
    public string Username           { get; set; } = "";
    public string Password           { get; set; } = "";
    public string AppId              { get; set; } = "OrderFlowEngine";
    public string AppVersion         { get; set; } = "1.0";
    public string DeviceId           { get; set; } = Guid.NewGuid().ToString();
    public int    Cid                { get; set; } = 0;
    public string Sec                { get; set; } = "";
    public string Symbol             { get; set; } = "MNQZ4";
    public bool   UseDemoEnvironment { get; set; } = true;
}

public sealed class SignalSettings
{
    public double TickSize              { get; set; } = 0.25;
    public int    PrimaryBarMinutes     { get; set; } = 3;
    public int    ConfirmBarMinutes     { get; set; } = 1;
    public int    SessionStartHourET    { get; set; } = 18;

    public double ValueAreaPercent      { get; set; } = 0.70;
    public int    RollingProfileBars    { get; set; } = 50;
    public double LvnThresholdPercent   { get; set; } = 0.15;

    public int    DivergenceLookback    { get; set; } = 5;

    public double ImpulseAtrMultiple    { get; set; } = 1.5;
    public int    ImpulseBars           { get; set; } = 3;
    public int    ObProximityTicks      { get; set; } = 10;

    public int    SignalCooldownBars    { get; set; } = 3;
}

public sealed class TradingSettings
{
    /// <summary>Master kill switch. Set true only after testing in demo.</summary>
    public bool   Enabled         { get; set; } = false;
    /// <summary>Fraction of account balance risked per trade (1% = 0.01).</summary>
    public double RiskPercent     { get; set; } = 0.01;
    /// <summary>Hard cap on contracts per signal.</summary>
    public int    MaxContracts    { get; set; } = 5;
    /// <summary>Stop placed this many ATR units from entry.</summary>
    public double StopAtrMultiple { get; set; } = 1.5;
    /// <summary>Target = stop distance × this ratio (2.0 = 2:1 R:R).</summary>
    public double RewardRatio     { get; set; } = 2.0;
    /// <summary>Dollar value per tick (MNQ = $0.50, NQ = $5.00).</summary>
    public double TickValue       { get; set; } = 0.50;
}

public sealed class AlertSettings
{
    public string NtfyUrl   { get; set; } = "";
    public string NtfyToken { get; set; } = "";
}
