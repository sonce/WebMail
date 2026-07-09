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

        // 滑动过期：超过 maxAgeSeconds 未被访问或写入的条目才清除。访问/写入会续期，故
        // 活跃轮询的邮箱缓存不会过期；停止访问满 maxAgeSeconds 后才失效。
        if (_cache.TryGetValue(buyerId, out var entry) && (now - entry.LastAccessedAt).TotalSeconds >= maxAgeSeconds)
        {
            _cache.TryRemove(buyerId, out _);
            entry = null;
        }

        // 软刷新窗口内：直接返回缓存并滑动续期。仅续「最后访问」时间，不影响按真实抓取时间
        // 计算的新鲜度，因此持续轮询仍会每 ttlSeconds 重取一次。
        if (!force && entry is not null && (now - entry.FetchedAt).TotalSeconds < ttlSeconds)
        {
            Touch(buyerId, entry, now);
            return new MailCacheResult(entry.Messages, Stale: false, Error: null);
        }

        try
        {
            var fetched = await FetchAsync(buyerId, cancellationToken);
            var views = ProjectLatest(fetched);
            _cache[buyerId] = new CacheEntry(views, now, now); // 写入即续期
            return new MailCacheResult(views, Stale: false, Error: null);
        }
        catch (AccountNotFoundException)
        {
            return new MailCacheResult(Array.Empty<MailMessageView>(), Stale: false, Error: "该买家未绑定邮箱账号");
        }
        catch (ProviderAuthorizationException)
        {
            await MarkBuyerAbnormalAsync(buyerId, cancellationToken);
            return Degraded(buyerId, "邮件刷新失败，且无历史数据", now);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Degraded(buyerId, "邮件刷新失败，以下为上次结果", now);
        }
    }

    private MailCacheResult Degraded(long buyerId, string emptyError, DateTimeOffset now)
    {
        if (_cache.TryGetValue(buyerId, out var stale) && stale.Messages.Count > 0)
        {
            // 降级读取也算访问，滑动续期：活跃轮询期间持续返回上次结果，停止访问后才过期。
            Touch(buyerId, stale, now);
            return new MailCacheResult(stale.Messages, Stale: true, Error: "邮件刷新失败，以下为上次结果");
        }
        return new MailCacheResult(Array.Empty<MailMessageView>(), Stale: false, Error: emptyError);
    }

    // 滑动续期：仅当条目未被并发抓取替换时更新「最后访问」时间，避免用旧消息覆盖新结果。
    private void Touch(long buyerId, CacheEntry entry, DateTimeOffset now)
    {
        _cache.TryUpdate(buyerId, entry with { LastAccessedAt = now }, entry);
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

    private sealed record CacheEntry(IReadOnlyList<MailMessageView> Messages, DateTimeOffset FetchedAt, DateTimeOffset LastAccessedAt);
}

internal sealed class AccountNotFoundException(long buyerId) : Exception($"No email account for buyer {buyerId}.");
