using Microsoft.Extensions.Options;

namespace SmartX.Api.Services;

/// <summary>
/// Every 2 s, recomputes each device's Online / Stale / Offline state from its last-seen time and reports
/// offline nodes to <see cref="AlertService"/>. Because the same offline node is reported again on every sweep,
/// this is where the <c>HashSet</c> de-duplication earns its keep (watch the "duplicates suppressed" counter).
/// </summary>
public sealed class DeviceWatchdog(DeviceRegistry registry, AlertService alerts, IOptions<GatewayOptions> options, ILogger<DeviceWatchdog> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stale = TimeSpan.FromSeconds(options.Value.StaleAfterSeconds);
        var offline = TimeSpan.FromSeconds(options.Value.OfflineAfterSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var (mac, name, justWentOffline) in registry.RefreshStatuses(DateTime.UtcNow, stale, offline))
                {
                    bool isNew = alerts.MarkDisconnected(mac, name);
                    if (isNew && justWentOffline) logger.LogDebug("Node {Name} ({Mac}) went offline.", name, mac);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
