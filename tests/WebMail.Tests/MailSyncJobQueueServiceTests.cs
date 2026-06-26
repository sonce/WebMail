using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services.Background;

namespace WebMail.Tests;

public sealed class MailSyncJobQueueServiceTests
{
    [Fact]
    public async Task QueueActiveWindowsSkipsBuyerWithExistingPendingJob()
    {
        await using var db = CreateDb();
        db.ActiveSyncWindows.Add(new ActiveSyncWindow { BuyerId = 10, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) });
        db.SyncJobs.Add(new SyncJob { BuyerId = 10, Status = SyncJobStatus.Pending });
        await db.SaveChangesAsync();

        var service = new MailSyncJobQueueService();

        var queued = await service.QueueActiveWindowJobsAsync(db, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(0, queued);
        Assert.Single(await db.SyncJobs.Where(x => x.BuyerId == 10).ToListAsync());
    }

    [Fact]
    public async Task QueueActiveWindowsSkipsBuyerWithExistingRunningJob()
    {
        await using var db = CreateDb();
        db.ActiveSyncWindows.Add(new ActiveSyncWindow { BuyerId = 11, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) });
        db.SyncJobs.Add(new SyncJob { BuyerId = 11, Status = SyncJobStatus.Running });
        await db.SaveChangesAsync();

        var service = new MailSyncJobQueueService();

        var queued = await service.QueueActiveWindowJobsAsync(db, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(0, queued);
        Assert.Single(await db.SyncJobs.Where(x => x.BuyerId == 11).ToListAsync());
    }

    [Fact]
    public async Task QueueActiveWindowsAddsJobForBuyerWithoutPendingOrRunningJob()
    {
        await using var db = CreateDb();
        db.ActiveSyncWindows.Add(new ActiveSyncWindow { BuyerId = 12, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) });
        await db.SaveChangesAsync();

        var service = new MailSyncJobQueueService();

        var queued = await service.QueueActiveWindowJobsAsync(db, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, queued);
        var job = Assert.Single(await db.SyncJobs.Where(x => x.BuyerId == 12).ToListAsync());
        Assert.Equal(SyncJobStatus.Pending, job.Status);
    }

    private static WebMailDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new WebMailDbContext(options);
    }
}
