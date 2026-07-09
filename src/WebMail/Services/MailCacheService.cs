using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services.EmailProviders;
using WebMail.Services.Security;

namespace WebMail.Services;

public sealed record MailCacheResult(
    IReadOnlyList<MailMessageView> Messages,
    bool Stale,
    string? Error);

public interface IMailCacheService
{
    Task<MailCacheResult> GetOrFetchAsync(long buyerId, bool force, CancellationToken cancellationToken);
}

public sealed partial class MailCacheService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration) : IMailCacheService
{
    private readonly ConcurrentDictionary<long, CacheEntry> _cache = new();

    public async Task<MailCacheResult> GetOrFetchAsync(long buyerId, bool force, CancellationToken cancellationToken)
    {
        var ttlSeconds = configuration.GetValue("MailSync:CacheTtlSeconds", 30);
        var maxAgeSeconds = configuration.GetValue("MailSync:CacheMaxAgeSeconds", 600);
        var now = DateTimeOffset.UtcNow;

        // 硬过期：超过最大存活时间的条目视为不存在并清除，避免长期故障时无限期返回旧数据。
        if (_cache.TryGetValue(buyerId, out var entry) && (now - entry.FetchedAt).TotalSeconds >= maxAgeSeconds)
        {
            _cache.TryRemove(buyerId, out _);
            entry = null;
        }

        if (!force && entry is not null && (now - entry.FetchedAt).TotalSeconds < ttlSeconds)
        {
            return new MailCacheResult(entry.Messages, Stale: false, Error: null);
        }

        try
        {
            var fetched = await FetchAsync(buyerId, cancellationToken);
            var views = ProjectLatest(fetched);
            _cache[buyerId] = new CacheEntry(views, now);
            return new MailCacheResult(views, Stale: false, Error: null);
        }
        catch (AccountNotFoundException)
        {
            return new MailCacheResult(Array.Empty<MailMessageView>(), Stale: false, Error: "该买家未绑定邮箱账号");
        }
        catch (ProviderAuthorizationException)
        {
            await MarkBuyerAbnormalAsync(buyerId, cancellationToken);
            return Degraded(buyerId, "邮件刷新失败，且无历史数据");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Degraded(buyerId, "邮件刷新失败，以下为上次结果");
        }
    }

    private MailCacheResult Degraded(long buyerId, string emptyError)
    {
        if (_cache.TryGetValue(buyerId, out var stale) && stale.Messages.Count > 0)
        {
            return new MailCacheResult(stale.Messages, Stale: true, Error: "邮件刷新失败，以下为上次结果");
        }
        return new MailCacheResult(Array.Empty<MailMessageView>(), Stale: false, Error: emptyError);
    }

    private async Task<IReadOnlyList<ProviderMessage>> FetchAsync(long buyerId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<WebMailDbContext>();

        var account = await db.EmailAccounts.FirstOrDefaultAsync(a => a.BuyerId == buyerId, cancellationToken);
        if (account is null)
        {
            throw new AccountNotFoundException(buyerId);
        }

        var days = configuration.GetValue("MailSync:InitialSyncDays", 30);
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var tokenProtector = sp.GetRequiredService<ITokenProtector>();
        var refreshToken = tokenProtector.Unprotect(account.EncryptedRefreshToken);
        var providers = sp.GetRequiredService<IEmailProviderResolver>();
        var provider = providers.Resolve(account.Provider);
        return await provider.FetchMessagesAsync(refreshToken, Array.Empty<string>(), since, cancellationToken);
    }

    private async Task MarkBuyerAbnormalAsync(long buyerId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WebMailDbContext>();
        var buyer = await db.Buyers.FirstOrDefaultAsync(b => b.Id == buyerId, cancellationToken);
        if (buyer is not null && buyer.EmailStatus == EmailAuthorizationStatus.Authorized)
        {
            buyer.EmailStatus = EmailAuthorizationStatus.Abnormal;
            db.AuditLogs.Add(new AuditLog { Action = "MailboxAbnormal", Details = $"buyer={buyerId}" });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    internal static IReadOnlyList<MailMessageView> ProjectLatest(IReadOnlyList<ProviderMessage> messages, int limit = 10) =>
        messages
            .OrderByDescending(m => m.SentAt)
            .Take(limit)
            .Select(m => new MailMessageView(
                m.ProviderMessageId,
                m.Sender,
                m.Subject,
                m.SentAt,
                m.Folder,
                m.TextBody,
                m.HtmlBody))
            .ToList();

    private sealed record CacheEntry(IReadOnlyList<MailMessageView> Messages, DateTimeOffset FetchedAt);
}

internal sealed class AccountNotFoundException(long buyerId) : Exception($"No email account for buyer {buyerId}.");
