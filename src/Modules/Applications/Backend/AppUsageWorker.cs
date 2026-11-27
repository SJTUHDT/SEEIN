using Seein.Core;
using Seein.Core.Apps;
using Seein.Core.Storage;
using Microsoft.Extensions.Hosting;

namespace Seein.Service;

/// <summary>Drives <see cref="AppUsageService"/> at the configured sampling interval.</summary>
public sealed class AppUsageWorker(AppUsageService usage, ILogger<AppUsageWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AppPaths.SystemDefault.EnsureCreated();
        var retryDelay = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await usage.EnsureInitializedAsync(stoppingToken).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Application usage database initialization failed; retrying in {DelaySeconds}s", retryDelay.TotalSeconds);
                try { await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 60));
            }
        }
        if (stoppingToken.IsCancellationRequested) return;

        logger.LogInformation("Application usage collector started");
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            try { await usage.TickAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Application usage sampling failed"); }

            var elapsed = (DateTimeOffset.UtcNow - started).TotalMilliseconds;
            var wait = TimeSpan.FromMilliseconds(Math.Max(500, 5000 - elapsed));
            try { await Task.Delay(wait, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
