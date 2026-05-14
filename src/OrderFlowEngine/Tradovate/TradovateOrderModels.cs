using System.Text.Json.Serialization;

namespace OrderFlowEngine.Tradovate;

// ── Account ───────────────────────────────────────────────────────────────────

public sealed record AccountInfo(
    [property: JsonPropertyName("id")]   int    Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("userId")] int  UserId);

public sealed record CashBalance(
    [property: JsonPropertyName("accountId")]  int    AccountId,
    [property: JsonPropertyName("cashBalance")] double Balance,
    [property: JsonPropertyName("openPl")]     double OpenPl,
    [property: JsonPropertyName("netLiq")]     double NetLiq);

// ── Order placement ───────────────────────────────────────────────────────────

public sealed record PlaceOrderRequest(
    [property: JsonPropertyName("accountSpec")]  string       AccountSpec,
    [property: JsonPropertyName("accountId")]    int          AccountId,
    [property: JsonPropertyName("action")]       string       Action,      // "Buy" | "Sell"
    [property: JsonPropertyName("symbol")]       string       Symbol,
    [property: JsonPropertyName("orderQty")]     int          OrderQty,
    [property: JsonPropertyName("orderType")]    string       OrderType,   // "Market"
    [property: JsonPropertyName("isAutomated")]  bool         IsAutomated,
    [property: JsonPropertyName("bracket1")]     BracketOrder Bracket1,   // stop loss
    [property: JsonPropertyName("bracket2")]     BracketOrder Bracket2);  // take profit

public sealed record BracketOrder(
    [property: JsonPropertyName("action")]    string  Action,
    [property: JsonPropertyName("orderType")] string  OrderType,
    [property: JsonPropertyName("price")]     double? Price,
    [property: JsonPropertyName("stopPrice")] double? StopPrice,
    [property: JsonPropertyName("orderQty")]  int     OrderQty);

public sealed record PlaceOrderResponse(
    [property: JsonPropertyName("orderId")]   int?    OrderId,
    [property: JsonPropertyName("failureReason")] string? FailureReason,
    [property: JsonPropertyName("errorText")] string? ErrorText);

// ── Computed result passed to SignalEngine ────────────────────────────────────

public sealed record PositionSize(
    int    Contracts,
    double StopPrice,
    double TargetPrice,
    double RiskDollars,
    double StopDistance);
