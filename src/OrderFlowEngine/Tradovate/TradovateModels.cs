using System.Text.Json.Serialization;

namespace OrderFlowEngine.Tradovate;

// ── REST auth ─────────────────────────────────────────────────────────────────

public sealed record AuthRequest(
    [property: JsonPropertyName("name")]       string Name,
    [property: JsonPropertyName("password")]   string Password,
    [property: JsonPropertyName("appId")]      string AppId,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("deviceId")]   string DeviceId,
    [property: JsonPropertyName("cid")]        int    Cid,
    [property: JsonPropertyName("sec")]        string Sec);

public sealed record AuthResponse(
    [property: JsonPropertyName("accessToken")]     string?   AccessToken,
    [property: JsonPropertyName("expirationTime")]  DateTime? ExpirationTime,
    [property: JsonPropertyName("userId")]          int?      UserId,
    [property: JsonPropertyName("errorText")]       string?   ErrorText,
    [property: JsonPropertyName("p-ticket")]        string?   PTicket,
    [property: JsonPropertyName("p-time")]          int?      PTime,
    [property: JsonPropertyName("p-captcha")]       bool?     PCaptcha);

// ── WebSocket wire types ──────────────────────────────────────────────────────

/// <summary>
/// Outer event envelope received from the Tradovate market-data WebSocket.
/// <c>E</c> = event type (e.g. "md", "props"); <c>D</c> = event payload.
/// </summary>
public sealed class WsEvent
{
    [JsonPropertyName("e")] public string?        E { get; init; }
    [JsonPropertyName("d")] public WsEventPayload? D { get; init; }
}

public sealed class WsEventPayload
{
    [JsonPropertyName("quotes")]       public QuoteData[]?    Quotes       { get; init; }
    [JsonPropertyName("subscriptionId")] public int?          SubscriptionId { get; init; }
    [JsonPropertyName("errorText")]    public string?         ErrorText    { get; init; }
}

public sealed class QuoteData
{
    [JsonPropertyName("timestamp")]  public string?        Timestamp  { get; init; }
    [JsonPropertyName("contractId")] public long           ContractId { get; init; }
    [JsonPropertyName("entries")]    public QuoteEntries?  Entries    { get; init; }
}

public sealed class QuoteEntries
{
    [JsonPropertyName("Bid")]   public QuoteEntry? Bid   { get; init; }
    [JsonPropertyName("Ask")]   public QuoteEntry? Ask   { get; init; }
    [JsonPropertyName("Trade")] public QuoteEntry? Trade { get; init; }
}

public sealed class QuoteEntry
{
    [JsonPropertyName("price")] public double? Price { get; init; }
    [JsonPropertyName("size")]  public double? Size  { get; init; }
}

// ── Domain events produced by TradovateClient ─────────────────────────────────

/// <summary>
/// Normalised tick emitted to the rest of the engine on every incoming trade print.
/// Bid and Ask are the quotes at the moment of the trade, used for delta classification.
/// </summary>
public sealed record Tick(
    DateTime Timestamp,
    double   Price,
    double   Volume,
    double   BidPrice,
    double   AskPrice)
{
    /// <summary>
    /// Aggressor-buy classification: trade at or above the ask.
    /// When bid/ask are not yet known, volume is split 50/50 downstream.
    /// </summary>
    public bool IsBuy => AskPrice > 0 && Price >= AskPrice;
}
