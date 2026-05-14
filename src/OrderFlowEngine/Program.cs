using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderFlowEngine.BarBuilding;
using OrderFlowEngine.Config;
using OrderFlowEngine.Engine;
using OrderFlowEngine.Tradovate;

// ── Host setup ────────────────────────────────────────────────────────────────
var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((_, config) =>
    {
        // appsettings.json is loaded by CreateDefaultBuilder automatically.
        // Override with environment variables at runtime (e.g. Docker secrets).
        // Key format: OrderFlow__Tradovate__Password=...
    })
    .ConfigureServices((ctx, services) =>
    {
        services.Configure<AppSettings>(ctx.Configuration.GetSection("OrderFlow"));

        services.AddSingleton<NtfyClient>();
        services.AddSingleton<TradovateClient>();
        services.AddSingleton<SignalEngine>();

        services.AddHostedService<EngineHostedService>();
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole(opts => opts.FormatterName = "simple");
    })
    .Build();

await host.RunAsync();

// ── Hosted service ────────────────────────────────────────────────────────────

/// <summary>
/// Bootstraps the SignalEngine and the TradovateClient, then blocks until
/// the application is stopped (Ctrl-C or SIGTERM).
/// </summary>
internal sealed class EngineHostedService : IHostedService
{
    private readonly TradovateClient _tradovate;
    private readonly SignalEngine    _engine;
    private readonly ILogger<EngineHostedService> _log;
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public EngineHostedService(
        TradovateClient tradovate,
        SignalEngine engine,
        ILogger<EngineHostedService> log)
    {
        _tradovate = tradovate;
        _engine    = engine;
        _log       = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts     = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runTask = _tradovate.RunAsync(_cts.Token);
        _log.LogInformation("OrderFlowEngine started. Waiting for market data…");
        return Task.CompletedTask;
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
