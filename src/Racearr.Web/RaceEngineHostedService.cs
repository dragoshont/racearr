using Racearr.Core;

namespace Racearr.Web;

/// <summary>
/// Hosts the race-engine control loop as a background service: prime the baseline once, then
/// drive <see cref="RaceEngine.TickAsync"/> on a fixed cadence (<c>POLL_SECONDS</c>).
/// </summary>
public sealed class RaceEngineHostedService(
    RacearrOptions options,
    RaceEngine engine,
    ILogger<RaceEngineHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "racearr starting | dry_run={DryRun} | instances={Instances} | pickup<{Pickup}s " +
            "speed>={Speed}MB/s@{SpeedWin}s target={Target}MB/s max/item={Max} protect_private={Protect}",
            options.DryRun, string.Join(",", engine.Instances.Select(i => i.Name)),
            options.PickupSlaSeconds, options.SpeedSlaMbps, options.SpeedSlaSeconds,
            options.RaceTargetMbps, options.MaxConcurrentPerItem, options.ProtectPrivate);

        try { await engine.PrimeBaselineAsync(stoppingToken); }
        catch (Exception ex) { logger.LogWarning(ex, "baseline prime failed"); }

        var period = TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds));
        using var timer = new PeriodicTimer(period);
        try
        {
            do
            {
                await engine.TickAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }
}
