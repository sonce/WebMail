using WebMail.Data;

namespace WebMail.Services.Background;

public sealed class MailSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    MailSyncJobQueueService queueService,
    ILogger<MailSyncBackgroundService> logger) : BackgroundService
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
            var processor = scope.ServiceProvider.GetRequiredService<MailSyncProcessor>();
            var now = DateTimeOffset.UtcNow;
            var queued = await queueService.QueueActiveWindowJobsAsync(db, now, cancellationToken);
            var processed = await processor.ProcessPendingAsync(db, now, cancellationToken);
            logger.LogInformation("Queued {Queued} and processed {Processed} sync jobs", queued, processed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Mail sync tick failed");
        }
    }
}
