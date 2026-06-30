using WebMail.Domain;
using WebMail.Services.EmailProviders;

namespace WebMail.Services;

public sealed record MailCacheResult(
    IReadOnlyList<MailMessageView> Messages,
    bool Stale,
    string? Error);

public sealed partial class MailCacheService
{
    internal static IReadOnlyList<MailMessageView> ProjectLatest(IReadOnlyList<ProviderMessage> messages, int limit = 10) =>
        messages
            .OrderByDescending(m => m.SentAt)
            .Take(limit)
            .Select(m => new MailMessageView(
                m.ProviderMessageId,
                m.Sender,
                m.Subject,
                m.SentAt,
                m.Folder))
            .ToList();
}
