namespace OrderFlowEngine.Config;

public sealed class AppSettings
{
    public TradovateSettings Tradovate { get; set; } = new();
    public SignalSettings    Signal    { get; set; } = new();
    public AlertSettings     Alerts    { get; set; } = new();
}

public sealed class TradovateSettings
{
    /// <summary>Tradovate account username.</summary>
    public string Username           { get; set; } = "";
    /// <summary>Tradovate account password.</summary>
    public string Password           { get; set; } = "";
    /// <summary>Application identifier registered with Tradovate developer portal.</summary>
    public string AppId              { get; set; } = "OrderFlowEngine";
    public string AppVersion         { get; set; } = "1.0";
    /// <summary>Any stable per-machine GUID; used by Tradovate for device fingerprinting.</summary>
    public string DeviceId           { get; set; } = Guid.NewGuid().ToString();
    /// <summary>Client ID from Tradovate developer portal (0 for personal apps).</summary>
    public int    Cid                { get; set; } = 0;
    /// <summary>Client secret paired with <see cref="Cid"/>.</summary>
    public string Sec                { get; set; } = "";
    /// <summary>Futures contract symbol, e.g. MNQZ4, NQZ4, ESZ4.</summary>
    public string Symbol             { get; set; } = "MNQZ4";
    /// <summary>True = use demo/paper environment; false = live.</summary>
    public bool   UseDemoEnvironment { get; set; } = true;
}

public sealed class SignalSettings
{
    /// <summary>Instrument minimum tick size (NQ/MNQ = 0.25).</summary>
    public double TickSize              { get; set; } = 0.25;
    /// <summary>Primary chart bar length in minutes (default 3).</summary>
    public int    PrimaryBarMinutes     { get; set; } = 3;
    /// <summary>Confirmation timeframe bar length in minutes (default 1).</summary>
    public int    ConfirmBarMinutes     { get; set; } = 1;
    /// <summary>Hour (ET) at which the volume profile resets for a new session (default 18 = 6 PM).</summary>
    public int    SessionStartHourET    { get; set; } = 18;

    // Volume Profile
    public double ValueAreaPercent      { get; set; } = 0.70;
    public int    RollingProfileBars    { get; set; } = 50;
    public double LvnThresholdPercent   { get; set; } = 0.15;

    // Cumulative Delta
    public int    DivergenceLookback    { get; set; } = 5;

    // Order Block
    public double ImpulseAtrMultiple    { get; set; } = 1.5;
    public int    ImpulseBars           { get; set; } = 3;
    public int    ObProximityTicks      { get; set; } = 10;

    // Gating
    public int    SignalCooldownBars    { get; set; } = 3;
}

public sealed class AlertSettings
{
    public string NtfyUrl   { get; set; } = "";
    public string NtfyToken { get; set; } = "";
}
