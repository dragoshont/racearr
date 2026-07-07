using Racearr.Core;

namespace Racearr.Web;

/// <summary>
/// Hosts the race-engine control loop as a background service.
/// <para>
/// PHASE 0: a heartbeat that only advances the loop counter, so the host, metrics and
/// <c>/status</c> are verifiable end-to-end. PHASE 1 replaces the loop body with the ported
/// pickup/speed-SLA racing logic (see ADR-0001).
/// </para>
/// </summary>
public sealed class RaceEngineHostedService(
    RacearrOptions options,
    RaceEngineState state,
    ILogger<RaceEngineHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.HasAnyInstance)
            logger.LogError("No *arr instance configured (need RADARR_API_KEY and/or SONARR_API_KEY).");

        logger.LogInformation(
            "racearr starting | dry_run={DryRun} | pickup<{Pickup}s speed>={Speed}MB/s@{SpeedWin}s " +
            "target={Target}MB/s max/item={Max} protect_private={Protect}",
            options.DryRun, options.PickupSlaSeconds, options.SpeedSlaMbps, options.SpeedSlaSeconds,
            options.RaceTargetMbps, options.MaxConcurrentPerItem, options.ProtectPrivate);

        var period = TimeSpan.FromSeconds(Math.Max(1, options.PollSeconds));
        using var timer = new PeriodicTimer(period);
        try
        {
            do
            {
                try
                {
                    // PHASE 1: evaluate pickup + speed SLAs and drive races here.
                    state.MarkLoop();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "loop iteration failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }
}
