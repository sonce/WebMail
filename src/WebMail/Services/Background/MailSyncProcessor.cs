using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services.EmailProviders;
using WebMail.Services.Security;

namespace WebMail.Services.Background;

public sealed class MailSyncProcessor(IEmailProviderResolver providers, IConfiguration configuration, ITokenProtector tokenProtector)
{
    public async Task<int> ProcessPendingAsync(WebMailDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pending = await db.SyncJobs
            .Where(x => x.Status == SyncJobStatus.Pending)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return 0;
        }

        var days = configuration.GetValue("MailSync:InitialSyncDays", 30);
        var since = now.AddDays(-days);
        var allowedSenders = await db.AllowedSenders.Select(x => x.EmailAddress).ToListAsync(cancellationToken);

        var processed = 0;
        foreach (var job in pending)
        {
            job.Status = SyncJobStatus.Running;
            job.StartedAt = now;
            await db.SaveChangesAsync(cancellationToken);

            try
            {
                var account = await db.EmailAccounts.FirstOrDefaultAsync(x => x.BuyerId == job.BuyerId, cancellationToken);
                if (account is not null)
                {
                    var provider = providers.Resolve(account.Provider);
                    var refreshToken = tokenProtector.Unprotect(account.EncryptedRefreshToken);
                    var messages = await provider.FetchMessagesAsync(refreshToken, allowedSenders, since, cancellationToken);
                    await UpsertMessagesAsync(db, account, messages, cancellationToken);
                }

                job.Status = SyncJobStatus.Succeeded;
                job.CompletedAt = now;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ProviderAuthorizationException)
            {
                var buyer = await db.Buyers.FirstOrDefaultAsync(b => b.Id == job.BuyerId, cancellationToken);
                if (buyer is not null && buyer.EmailStatus == EmailAuthorizationStatus.Authorized)
                {
                    buyer.EmailStatus = EmailAuthorizationStatus.Abnormal;
                    db.AuditLogs.Add(new AuditLog { Action = "MailboxAbnormal", Details = $"buyer={job.BuyerId}" });
                }
                job.Status = SyncJobStatus.Failed;
                job.Error = "authorization";
                job.CompletedAt = now;
            }
            catch (Exception ex)
            {
                job.Status = SyncJobStatus.Failed;
                job.Error = ex.Message;
                job.CompletedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);
            processed++;
        }

        return processed;
    }

    private static async Task UpsertMessagesAsync(WebMailDbContext db, EmailAccount account, IReadOnlyList<ProviderMessage> messages, CancellationToken cancellationToken)
    {
        foreach (var m in messages)
        {
            var existing = await db.EmailMessages.FirstOrDefaultAsync(
                x => x.EmailAccountId == account.Id && x.ProviderMessageId == m.ProviderMessageId,
                cancellationToken);

            if (existing is null)
            {
                db.EmailMessages.Add(new EmailMessage
                {
                    BuyerId = account.BuyerId,
                    EmailAccountId = account.Id,
                    ProviderMessageId = m.ProviderMessageId,
                    ProviderThreadId = m.ProviderThreadId,
                    Sender = m.Sender,
                    Recipients = m.Recipients,
                    Subject = m.Subject,
                    SentAt = m.SentAt,
                    TextBody = m.TextBody,
                    HtmlBody = m.HtmlBody,
                    AttachmentMetadataJson = m.AttachmentMetadataJson,
                    Folder = m.Folder
                });
            }
            else
            {
                existing.ProviderThreadId = m.ProviderThreadId;
                existing.Sender = m.Sender;
                existing.Recipients = m.Recipients;
                existing.Subject = m.Subject;
                existing.SentAt = m.SentAt;
                existing.TextBody = m.TextBody;
                existing.HtmlBody = m.HtmlBody;
                existing.AttachmentMetadataJson = m.AttachmentMetadataJson;
                existing.Folder = m.Folder;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
