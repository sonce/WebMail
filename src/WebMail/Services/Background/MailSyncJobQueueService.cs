using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;

namespace WebMail.Services.Background;

public sealed class MailSyncJobQueueService
{
    public async Task<int> QueueActiveWindowJobsAsync(WebMailDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var activeBuyerIds = await db.ActiveSyncWindows
            .Where(x => x.ExpiresAt > now)
            .Select(x => x.BuyerId)
            .ToListAsync(cancellationToken);

        if (activeBuyerIds.Count == 0)
        {
            return 0;
        }

        var existingBuyerIds = await db.SyncJobs
            .Where(x => activeBuyerIds.Contains(x.BuyerId)
                && (x.Status == SyncJobStatus.Pending || x.Status == SyncJobStatus.Running))
            .Select(x => x.BuyerId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var existing = existingBuyerIds.ToHashSet();
        var queued = 0;
        foreach (var buyerId in activeBuyerIds.Distinct())
        {
            if (existing.Contains(buyerId))
            {
                continue;
            }

            db.SyncJobs.Add(new SyncJob { BuyerId = buyerId, Status = SyncJobStatus.Pending });
            queued++;
        }

        if (queued > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return queued;
    }
}
