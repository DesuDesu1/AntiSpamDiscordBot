using AntiSpam.Bot.Data;
using Microsoft.EntityFrameworkCore;

namespace AntiSpam.Bot.Features.Moderation;

/// <summary>
/// Periodically deletes spam incidents older than the retention window so the
/// table can't grow without bound. Resolved and still-pending incidents are both
/// purged: a week-old alert is stale, and its Ban/Release buttons simply go inert
/// (the interaction handler no-ops when the incident is gone) — a moderator can
/// still ban or unmute the user directly in Discord.
/// </summary>
public class IncidentCleanupWorker : BackgroundService
{
    private const int RetentionDays = 7;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    private readonly IDbContextFactory<BotDbContext> _dbFactory;
    private readonly ILogger<IncidentCleanupWorker> _logger;

    public IncidentCleanupWorker(
        IDbContextFactory<BotDbContext> dbFactory,
        ILogger<IncidentCleanupWorker> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await PurgeAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PurgeAsync(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var deleted = await db.SpamIncidents
                .Where(i => i.CreatedAt < cutoff)
                .ExecuteDeleteAsync(ct);

            if (deleted > 0)
                _logger.LogInformation("Purged {Count} spam incidents older than {Days} days",
                    deleted, RetentionDays);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to purge old spam incidents");
        }
    }
}
