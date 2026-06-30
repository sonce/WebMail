using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using WebMail.Services.EmailProviders;
using WebMail.Services.Security;
using Xunit;

namespace WebMail.Tests;

public sealed class MailCacheServiceTests
{
    [Fact]
    public void ProjectLatestKeepsLimitNewestBySentAtDescending()
    {
        var baseTime = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
        var messages = Enumerable.Range(0, 15)
            .Select(i => new ProviderMessage(
                $"m-{i}", null, "s@x.com", "r@x.com", "sub",
                baseTime.AddMinutes(i), null, null, null, MailFolder.Inbox))
            .ToArray();

        var result = MailCacheService.ProjectLatest(messages, limit: 10);

        Assert.Equal(10, result.Count);
        // 最新的 10 条 = m-6 .. m-14，倒序排列（m-14 在前）
        Assert.Equal("m-14", result[0].Id);
        Assert.Equal("m-5", result[9].Id);
    }

    [Fact]
    public void ProjectLatestMapsFieldsAndFolder()
    {
        var sentAt = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        var messages = new[]
        {
            new ProviderMessage("id-1", "t", "sender@x.com", "r", "Hello",
                sentAt, "body", null, null, MailFolder.Junk)
        };

        var result = MailCacheService.ProjectLatest(messages, limit: 10);

        var view = Assert.Single(result);
        Assert.Equal("id-1", view.Id);
        Assert.Equal("sender@x.com", view.Sender);
        Assert.Equal("Hello", view.Subject);
        Assert.Equal(sentAt, view.SentAt);
        Assert.Equal(MailFolder.Junk, view.Folder);
    }

    [Fact]
    public void ProjectLatestReturnsAllWhenFewerThanLimit()
    {
        var messages = new[]
        {
            new ProviderMessage("m-0", null, "s", "r", "x",
                DateTimeOffset.UtcNow, null, null, null, MailFolder.Inbox)
        };

        var result = MailCacheService.ProjectLatest(messages, limit: 10);

        Assert.Single(result);
    }

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static MailCacheService CreateService(IEmailProvider provider, WebMailDbContext? db = null, int ttlSeconds = 30)
    {
        var resolver = new EmailProviderResolver([provider]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MailSync:InitialSyncDays"] = "30",
                ["MailSync:CacheTtlSeconds"] = ttlSeconds.ToString()
            })
            .Build();
        var scopeFactory = new StubScopeFactory(db ?? CreateDb());
        return new MailCacheService(scopeFactory, resolver, config, new FakeTokenProtector());
    }

    private static EmailAccount Account(long buyerId = 1, long accountId = 1, string provider = "Fake") => new()
    {
        Id = accountId, BuyerId = buyerId, Email = "b@x.com",
        Provider = provider, ProviderUserId = "p", EncryptedRefreshToken = "enc:token"
    };

    private static ProviderMessage Msg(string id, int minOffset = 0, MailFolder folder = MailFolder.Inbox) =>
        new(id, null, "s@x.com", "r@x.com", "sub",
            new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero).AddMinutes(minOffset),
            null, null, null, folder);

    [Fact]
    public async Task GetOrFetchReturnsCachedWithinTtlWithoutRefetch()
    {
        await using var db = CreateDb();
        db.EmailAccounts.Add(Account());
        await db.SaveChangesAsync();
        var provider = new CountingProvider("Fake", new[] { Msg("m-1") });
        var svc = CreateService(provider, db, ttlSeconds: 30);

        await svc.GetOrFetchAsync(buyerId: 1, force: false, CancellationToken.None);
        await svc.GetOrFetchAsync(buyerId: 1, force: false, CancellationToken.None);

        Assert.Equal(1, provider.FetchCount);
    }

    [Fact]
    public async Task GetOrFetchForcesRefreshWhenForced()
    {
        await using var db = CreateDb();
        db.EmailAccounts.Add(Account());
        await db.SaveChangesAsync();
        var provider = new CountingProvider("Fake", new[] { Msg("m-1") });
        var svc = CreateService(provider, db, ttlSeconds: 30);

        await svc.GetOrFetchAsync(buyerId: 1, force: false, CancellationToken.None);
        await svc.GetOrFetchAsync(buyerId: 1, force: true, CancellationToken.None);

        Assert.Equal(2, provider.FetchCount);
    }

    [Fact]
    public async Task GetOrFetchReturnsStaleCacheWithErrorOnFetchFailure()
    {
        await using var db = CreateDb();
        db.EmailAccounts.Add(Account());
        await db.SaveChangesAsync();
        var provider = new ToggleProvider("Fake", new[] { Msg("m-1") });
        var svc = CreateService(provider, db, ttlSeconds: 30);

        var first = await svc.GetOrFetchAsync(buyerId: 1, force: true, CancellationToken.None);
        provider.Throw = true;
        var second = await svc.GetOrFetchAsync(buyerId: 1, force: true, CancellationToken.None);

        Assert.False(first.Stale);
        Assert.True(second.Stale);
        Assert.NotNull(second.Error);
        Assert.Single(second.Messages);
        Assert.Equal("m-1", second.Messages[0].Id);
    }

    [Fact]
    public async Task GetOrFetchReturnsEmptyWithErrorOnFirstFailure()
    {
        await using var db = CreateDb();
        db.EmailAccounts.Add(Account(provider: "Boom"));
        await db.SaveChangesAsync();
        var provider = new ThrowingProvider("Boom");
        var svc = CreateService(provider, db, ttlSeconds: 30);

        var result = await svc.GetOrFetchAsync(buyerId: 1, force: true, CancellationToken.None);

        Assert.Empty(result.Messages);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task GetOrFetchReturnsEmptyWithErrorWhenNoAccount()
    {
        await using var db = CreateDb();
        var provider = new CountingProvider("Fake", new[] { Msg("m-1") });
        var svc = CreateService(provider, db, ttlSeconds: 30);

        var result = await svc.GetOrFetchAsync(buyerId: 1, force: true, CancellationToken.None);

        Assert.Empty(result.Messages);
        Assert.NotNull(result.Error);
        Assert.Equal(0, provider.FetchCount);
    }

    [Fact]
    public async Task GetOrFetchFlipsBuyerToAbnormalOnAuthFailure()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer
        {
            Id = 1, CardNo = "c1", EmailStatus = EmailAuthorizationStatus.Authorized,
            Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Approved
        });
        db.EmailAccounts.Add(Account(provider: "AuthBoom"));
        await db.SaveChangesAsync();
        var provider = new AuthThrowingProvider("AuthBoom");
        var svc = CreateService(provider, db, ttlSeconds: 30);

        await svc.GetOrFetchAsync(buyerId: 1, force: true, CancellationToken.None);

        var buyer = await db.Buyers.SingleAsync(x => x.Id == 1);
        Assert.Equal(EmailAuthorizationStatus.Abnormal, buyer.EmailStatus);
    }

    // ---- test doubles ----

    private sealed class CountingProvider(string name, IReadOnlyList<ProviderMessage> messages) : IEmailProvider
    {
        public string Name { get; } = name;
        public int FetchCount { get; private set; }
        public OAuthStartResult BuildAuthorizationUrl(string cardNo, string redirectUri) => new(cardNo, cardNo);
        public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, string redirectUri, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string rt, IReadOnlyCollection<string> s, DateTimeOffset? since, CancellationToken ct)
        { FetchCount++; return Task.FromResult(messages); }
    }

    private sealed class ToggleProvider(string name, IReadOnlyList<ProviderMessage> messages) : IEmailProvider
    {
        public string Name { get; } = name;
        public bool Throw { get; set; }
        public OAuthStartResult BuildAuthorizationUrl(string cardNo, string redirectUri) => new(cardNo, cardNo);
        public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, string redirectUri, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string rt, IReadOnlyCollection<string> s, DateTimeOffset? since, CancellationToken ct)
            => Throw ? throw new InvalidOperationException("network down") : Task.FromResult(messages);
    }

    private sealed class ThrowingProvider(string name) : IEmailProvider
    {
        public string Name { get; } = name;
        public OAuthStartResult BuildAuthorizationUrl(string cardNo, string redirectUri) => new(cardNo, cardNo);
        public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, string redirectUri, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string rt, IReadOnlyCollection<string> s, DateTimeOffset? since, CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    private sealed class AuthThrowingProvider(string name) : IEmailProvider
    {
        public string Name { get; } = name;
        public OAuthStartResult BuildAuthorizationUrl(string cardNo, string redirectUri) => new(cardNo, cardNo);
        public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, string redirectUri, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string rt, IReadOnlyCollection<string> s, DateTimeOffset? since, CancellationToken ct) => throw new ProviderAuthorizationException("auth failed");
    }

    private sealed class StubScopeFactory(WebMailDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new StubScope(db);
        private sealed class StubScope(WebMailDbContext db) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new StubProvider(db);
            public void Dispose() { }
        }
        private sealed class StubProvider(WebMailDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType == typeof(WebMailDbContext) ? db : null;
        }
    }
}
