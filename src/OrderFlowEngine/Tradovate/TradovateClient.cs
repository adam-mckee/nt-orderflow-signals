using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlowEngine.Config;

namespace OrderFlowEngine.Tradovate;

/// <summary>
/// Manages authentication and the Tradovate market-data WebSocket connection.
///
/// Protocol notes (Tradovate custom wire format):
///   Client → Server text frame:  "[frame-id]\n[endpoint]\n\n[json-body]"
///   Server → Client text frame:  JSON object  {"e":"[event]","d":{...}}
///   Server → Client binary frame: single 0x00 byte = heartbeat; must echo back.
///
/// After connecting, the client sends an "authorize" frame, then subscribes to
/// quotes for the configured symbol. All trade ticks are emitted via <see cref="OnTick"/>.
/// </summary>
public sealed class TradovateClient : IAsyncDisposable
{
    // ── Endpoints ─────────────────────────────────────────────────────────────
    private const string LiveAuthUrl = "https://live.tradovate.com/v1/auth/accesstokenrequest";
    private const string DemoAuthUrl = "https://demo.tradovate.com/v1/auth/accesstokenrequest";
    private const string LiveWsUrl   = "wss://md.tradovate.com/v1/websocket";
    private const string DemoWsUrl   = "wss://md-demo.tradovate.com/v1/websocket";

    private static readonly TimeSpan TokenRefreshBuffer = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReconnectBase      = TimeSpan.FromSeconds(2);
    private const int MaxReconnectAttempts = 8;

    // ── Dependencies ──────────────────────────────────────────────────────────
    private readonly TradovateSettings   _cfg;
    private readonly ILogger<TradovateClient> _log;
    private readonly HttpClient          _http = new();

    // ── State ─────────────────────────────────────────────────────────────────
    private ClientWebSocket? _ws;
    private string?          _accessToken;
    private DateTime         _tokenExpiry;
    private int              _frameId;
    private double           _lastBid;
    private double           _lastAsk;

    public event Action<Tick>? OnTick;

    public TradovateClient(IOptions<AppSettings> opts, ILogger<TradovateClient> log)
    {
        _cfg = opts.Value.Tradovate;
        _log = log;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Authenticates, opens the WebSocket, subscribes to quotes, and starts the
    /// receive loop. Reconnects automatically with exponential back-off.
    /// Call once; runs until <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EnsureTokenAsync(ct);
                await ConnectAndSubscribeAsync(ct);
                attempt = 0;
                await ReceiveLoopAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                attempt = Math.Min(attempt + 1, MaxReconnectAttempts);
                var delay = ReconnectBase * Math.Pow(2, attempt - 1);
                _log.LogWarning(ex, "WebSocket disconnected. Reconnecting in {Delay:g} (attempt {N})", delay, attempt);
                await Task.Delay(delay, ct);
            }
            finally
            {
                _ws?.Dispose();
                _ws = null;
            }
        }
    }

    // ── Authentication ────────────────────────────────────────────────────────

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_accessToken != null && DateTime.UtcNow < _tokenExpiry - TokenRefreshBuffer)
            return;

        _log.LogInformation("Authenticating with Tradovate ({Env})…",
            _cfg.UseDemoEnvironment ? "demo" : "live");

        var req = new AuthRequest(
            _cfg.Username, _cfg.Password, _cfg.AppId, _cfg.AppVersion,
            _cfg.DeviceId, _cfg.Cid, _cfg.Sec);

        string url  = _cfg.UseDemoEnvironment ? DemoAuthUrl : LiveAuthUrl;
        string body = JsonSerializer.Serialize(req);

        using var resp = await _http.PostAsync(url,
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();

        var auth = JsonSerializer.Deserialize<AuthResponse>(
            await resp.Content.ReadAsStringAsync(ct))
            ?? throw new InvalidOperationException("Empty auth response.");

        if (!string.IsNullOrEmpty(auth.ErrorText))
            throw new InvalidOperationException($"Tradovate auth error: {auth.ErrorText}");
        if (string.IsNullOrEmpty(auth.AccessToken))
            throw new InvalidOperationException("Tradovate returned no access token.");

        _accessToken = auth.AccessToken;
        _tokenExpiry  = auth.ExpirationTime ?? DateTime.UtcNow.AddHours(24);
        _log.LogInformation("Authenticated. Token expires {Expiry:u}", _tokenExpiry);
    }

    // ── WebSocket lifecycle ───────────────────────────────────────────────────

    private async Task ConnectAndSubscribeAsync(CancellationToken ct)
    {
        string wsUrl = _cfg.UseDemoEnvironment ? DemoWsUrl : LiveWsUrl;
        _log.LogInformation("Connecting to {Url}", wsUrl);

        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(wsUrl), ct);
        _log.LogDebug("WebSocket connected.");

        // Authenticate on the socket
        await SendFrameAsync("authorize", new { token = _accessToken }, ct);

        // Subscribe to quote updates for the configured symbol
        await SendFrameAsync("md/subscribeQuote", new { symbol = _cfg.Symbol }, ct);
        _log.LogInformation("Subscribed to quotes for {Symbol}", _cfg.Symbol);
    }

    // ── Receive loop ──────────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[16 * 1024];

        while (!ct.IsCancellationRequested && _ws!.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await _ws.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;

                // Binary frame = heartbeat; echo back immediately
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    await _ws.SendAsync(buf.AsMemory(0, result.Count),
                        WebSocketMessageType.Binary, true, ct);
                    goto nextMessage;
                }

                ms.Write(buf, 0, result.Count);
            }
            while (!result.EndOfMessage);

            HandleTextFrame(Encoding.UTF8.GetString(ms.ToArray()));

            nextMessage:;
        }
    }

    // ── Message parsing ───────────────────────────────────────────────────────

    private void HandleTextFrame(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            // Tradovate sends JSON event objects directly as text frames.
            // Some frames may be acknowledgments: "[frame-id]\n{...}"
            // Strip a leading frame-id line if present.
            string json = text;
            int newline = text.IndexOf('\n');
            if (newline > 0 && int.TryParse(text[..newline], out _))
                json = text[(newline + 1)..];

            var evt = JsonSerializer.Deserialize<WsEvent>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (evt == null) return;

            if (evt.E == "md" && evt.D?.Quotes != null)
                foreach (var q in evt.D.Quotes)
                    ProcessQuote(q);
        }
        catch (JsonException)
        {
            // Non-JSON frames (e.g. plain "o" open marker) — ignore.
        }
    }

    private void ProcessQuote(QuoteData q)
    {
        var entries = q.Entries;
        if (entries == null) return;

        // Update last known bid/ask
        if (entries.Bid?.Price is double bid && bid > 0)  _lastBid = bid;
        if (entries.Ask?.Price is double ask && ask > 0)  _lastAsk = ask;

        // Only emit a tick when a trade print is present
        if (entries.Trade?.Price is not double price || price <= 0) return;
        double volume = entries.Trade.Size ?? 1;

        var tick = new Tick(
            Timestamp: DateTime.UtcNow,
            Price:     price,
            Volume:    volume,
            BidPrice:  _lastBid,
            AskPrice:  _lastAsk);

        OnTick?.Invoke(tick);
    }

    // ── Frame sending ─────────────────────────────────────────────────────────

    private async Task SendFrameAsync(string endpoint, object body, CancellationToken ct)
    {
        int id  = Interlocked.Increment(ref _frameId);
        string frame = $"{id}\n{endpoint}\n\n{JsonSerializer.Serialize(body)}";
        await _ws!.SendAsync(Encoding.UTF8.GetBytes(frame),
            WebSocketMessageType.Text, true, ct);
        _log.LogDebug("→ {Frame}", frame);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws != null)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", default); }
            catch { /* ignore */ }
            _ws.Dispose();
        }
        _http.Dispose();
    }
}
