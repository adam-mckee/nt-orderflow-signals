using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlowEngine.Config;

namespace OrderFlowEngine.Engine;

/// <summary>
/// Fire-and-forget ntfy push client.
/// POSTs a JSON payload to the configured topic URL; never blocks the signal loop.
/// </summary>
public sealed class NtfyClient
{
    private static readonly HttpClient _http = new();
    private readonly AlertSettings     _cfg;
    private readonly ILogger<NtfyClient> _log;

    public NtfyClient(IOptions<AppSettings> opts, ILogger<NtfyClient> log)
    {
        _cfg = opts.Value.Alerts;
        _log = log;
    }

    public void Send(string symbol, string direction, double price, string reason,
                     int contracts, double stopPrice, double targetPrice)
    {
        string url = _cfg.NtfyUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        string token     = _cfg.NtfyToken;
        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        string json = string.Format(
            "{{\"symbol\":\"{0}\",\"direction\":\"{1}\",\"price\":{2}," +
            "\"contracts\":{5},\"stopPrice\":{6},\"targetPrice\":{7}," +
            "\"timestamp\":\"{3}\",\"triggerReason\":\"{4}\"}}",
            Esc(symbol), direction, price, timestamp, Esc(reason),
            contracts, stopPrice, targetPrice);

        _ = Task.Run(async () =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrEmpty(token))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    _log.LogWarning("ntfy returned {Code} for {Url}", (int)resp.StatusCode, url);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "ntfy alert failed — continuing.");
            }
        });
    }

    private static string Esc(string s)
        => s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
}
