using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlowEngine.Config;

namespace OrderFlowEngine.Tradovate;

/// <summary>
/// Handles Tradovate REST API calls for account info, balance, and order placement.
/// Maintains its own access token (independent of the WebSocket auth in TradovateClient)
/// so the two concerns don't block each other.
///
/// All endpoints: https://live.tradovate.com/v1/ or https://demo.tradovate.com/v1/
/// </summary>
public sealed class TradovateOrderClient
{
    private const string LiveBase = "https://live.tradovate.com/v1";
    private const string DemoBase = "https://demo.tradovate.com/v1";
    private static readonly TimeSpan TokenBuffer = TimeSpan.FromMinutes(5);

    private readonly TradovateSettings _cfg;
    private readonly TradingSettings   _trade;
    private readonly ILogger<TradovateOrderClient> _log;
    private readonly HttpClient _http = new();

    private string?   _token;
    private DateTime  _tokenExpiry;
    private AccountInfo? _account;

    private string Base => _cfg.UseDemoEnvironment ? DemoBase : LiveBase;

    public TradovateOrderClient(IOptions<AppSettings> opts, ILogger<TradovateOrderClient> log)
    {
        _cfg   = opts.Value.Tradovate;
        _trade = opts.Value.Trading;
        _log   = log;
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    /// <summary>Authenticate and cache account info. Call once at startup.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await EnsureTokenAsync(ct);
        var accounts = await GetAsync<AccountInfo[]>("/account/list", ct)
                       ?? throw new InvalidOperationException("No accounts returned.");
        _account = accounts[0];
        _log.LogInformation("Order client ready — account: {Name} (id={Id})", _account.Name, _account.Id);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Fetches the current net liquidation value of the account.</summary>
    public async Task<double> GetAccountBalanceAsync(CancellationToken ct)
    {
        await EnsureTokenAsync(ct);
        var cb = await GetAsync<CashBalance>(
            $"/cashbalance/getcashbalancebyaccount?accountId={_account!.Id}", ct);
        return cb?.NetLiq ?? 0;
    }

    /// <summary>
    /// Places a market-entry bracket order (entry + stop + target as OSO).
    /// Returns the Tradovate order ID on success.
    /// </summary>
    public async Task<int?> PlaceBracketOrderAsync(
        string direction, int contracts, double stopPrice, double targetPrice,
        CancellationToken ct)
    {
        await EnsureTokenAsync(ct);

        bool isLong = direction == "Long";
        string entryAction = isLong ? "Buy"  : "Sell";
        string exitAction  = isLong ? "Sell" : "Buy";

        // bracket1 = stop loss, bracket2 = take profit
        var req = new PlaceOrderRequest(
            AccountSpec:  _account!.Name,
            AccountId:    _account.Id,
            Action:       entryAction,
            Symbol:       _cfg.Symbol,
            OrderQty:     contracts,
            OrderType:    "Market",
            IsAutomated:  true,
            Bracket1: new BracketOrder(
                Action:    exitAction,
                OrderType: "Stop",
                StopPrice: Math.Round(stopPrice,   2),
                Price:     null,
                OrderQty:  contracts),
            Bracket2: new BracketOrder(
                Action:    exitAction,
                OrderType: "Limit",
                Price:     Math.Round(targetPrice, 2),
                StopPrice: null,
                OrderQty:  contracts));

        _log.LogInformation(
            "Placing {Dir} {Qty} {Sym} — stop={Stop:F2} target={Target:F2}",
            direction, contracts, _cfg.Symbol, stopPrice, targetPrice);

        var resp = await PostAsync<PlaceOrderRequest, PlaceOrderResponse>(
            "/order/placeorder", req, ct);

        if (resp == null || !string.IsNullOrEmpty(resp.ErrorText))
        {
            _log.LogError("Order failed: {Err}", resp?.ErrorText ?? "null response");
            return null;
        }
        if (!string.IsNullOrEmpty(resp.FailureReason))
        {
            _log.LogError("Order rejected: {Reason}", resp.FailureReason);
            return null;
        }

        _log.LogInformation("Order placed — id={Id}", resp.OrderId);
        return resp.OrderId;
    }

    // ── Auth ──────────────────────────────────────────────────────────────────

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_token != null && DateTime.UtcNow < _tokenExpiry - TokenBuffer) return;

        var req = new AuthRequest(
            _cfg.Username, _cfg.Password, _cfg.AppId, _cfg.AppVersion,
            _cfg.DeviceId, _cfg.Cid, _cfg.Sec);

        string json = JsonSerializer.Serialize(req);
        using var resp = await _http.PostAsync(
            $"{Base}/auth/accesstokenrequest",
            new StringContent(json, Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();

        var auth = JsonSerializer.Deserialize<AuthResponse>(
            await resp.Content.ReadAsStringAsync(ct));

        if (auth == null || string.IsNullOrEmpty(auth.AccessToken))
            throw new InvalidOperationException("Order client auth failed.");

        _token       = auth.AccessToken;
        _tokenExpiry = auth.ExpirationTime ?? DateTime.UtcNow.AddHours(24);
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _token);
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"{Base}{path}", ct);
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<T>(await resp.Content.ReadAsStringAsync(ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private async Task<TResp?> PostAsync<TReq, TResp>(string path, TReq body, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(body);
        using var resp = await _http.PostAsync($"{Base}{path}",
            new StringContent(json, Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<TResp>(await resp.Content.ReadAsStringAsync(ct),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
}
