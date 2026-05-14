using Microsoft.Extensions.Options;
using OrderFlowEngine.Config;
using OrderFlowEngine.Engine;
using OrderFlowEngine.Tradovate;

// ── Host setup ────────────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

// Config: appsettings.json loaded automatically; override via environment vars.
// Key format: OrderFlow__Tradovate__Password=...
builder.Services.Configure<AppSettings>(builder.Configuration.GetSection("OrderFlow"));

// Blazor Server
builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

// Engine singletons
builder.Services.AddSingleton<NtfyClient>();
builder.Services.AddSingleton<TradovateClient>();
builder.Services.AddSingleton<TradovateOrderClient>();
builder.Services.AddSingleton<PositionSizer>();
builder.Services.AddSingleton<TradeManager>();
builder.Services.AddSingleton<DashboardState>();
builder.Services.AddSingleton<SignalEngine>();

builder.Services.AddHostedService<EngineHostedService>();

builder.Logging.ClearProviders();
builder.Logging.AddConsole(opts => opts.FormatterName = "simple");

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<OrderFlowEngine.Components.App>()
   .AddInteractiveServerRenderMode();

await app.RunAsync();

// ── Hosted service ────────────────────────────────────────────────────────────

/// <summary>
/// Bootstraps the SignalEngine and TradovateClient, then blocks until stopped.
/// If Trading.Enabled is true, also initialises the REST order client.
/// </summary>
internal sealed class EngineHostedService : IHostedService
{
    private readonly TradovateClient      _tradovate;
    private readonly TradovateOrderClient _orderClient;
    private readonly SignalEngine         _engine;
    private readonly IOptions<AppSettings> _opts;
    private readonly ILogger<EngineHostedService> _log;
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public EngineHostedService(
        TradovateClient tradovate,
        TradovateOrderClient orderClient,
        SignalEngine engine,
        IOptions<AppSettings> opts,
        ILogger<EngineHostedService> log)
    {
        _tradovate   = tradovate;
        _orderClient = orderClient;
        _engine      = engine;
        _opts        = opts;
        _log         = log;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (_opts.Value.Trading.Enabled)
        {
            _log.LogInformation("Trading enabled — initialising Tradovate order client…");
            await _orderClient.InitializeAsync(_cts.Token);
        }
        else
        {
            _log.LogInformation("Trading disabled — running in paper-signal mode.");
        }

        _runTask = _tradovate.RunAsync(_cts.Token);
        _log.LogInformation("OrderFlowEngine started. Dashboard: http://localhost:5000");
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _log.LogInformation("Shutting down…");
        _cts?.Cancel();
        if (_runTask != null)
            await Task.WhenAny(_runTask, Task.Delay(Timeout.Infinite, ct));
        await _engine.DisposeAsync();
        await _tradovate.DisposeAsync();
    }
}
