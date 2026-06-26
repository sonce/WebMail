using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;

namespace WebMail.Services.Background;

public sealed class MailSyncBackgroundService(IServiceScopeFactory scopeFactory, ILogger<MailSyncBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await TickAsync(stoppingToken);
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WebMailDbContext>();
            var now = DateTimeOffset.UtcNow;
            var activeBuyerIds = await db.ActiveSyncWindows.Where(x => x.ExpiresAt > now).Select(x => x.BuyerId).ToListAsync(cancellationToken);
            foreach (var buyerId in activeBuyerIds) db.SyncJobs.Add(new SyncJob { BuyerId = buyerId, Status = SyncJobStatus.Pending });
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Queued {Count} active buyer sync jobs", activeBuyerIds.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Mail sync tick failed");
        }
    }
}
