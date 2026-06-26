using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services.Background;
using WebMail.Services.EmailProviders;
using Xunit;

namespace WebMail.Tests;

public sealed class MailSyncProcessorTests
{
    [Fact]
    public async Task ProcessPendingStoresMessagesAndMarksJobSucceeded()
    {
        await using var db = CreateDb();
        SeedAccount(db, buyerId: 1, accountId: 1, provider: "Fake");
        db.SyncJobs.Add(new SyncJob { BuyerId = 1, Status = SyncJobStatus.Pending });
        await db.SaveChangesAsync();

        var processor = CreateProcessor(new FakeProvider("Fake",
        [
            Message("m-1", MailFolder.Inbox),
            Message("m-2", MailFolder.Junk)
        ]));

        var processed = await processor.ProcessPendingAsync(db, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, processed);
        Assert.Equal(SyncJobStatus.Succeeded, (await db.SyncJobs.SingleAsync()).Status);
        var stored = await db.EmailMessages.OrderBy(x => x.ProviderMessageId).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Equal(MailFolder.Junk, stored.Single(x => x.ProviderMessageId == "m-2").Folder);
    }

    [Fact]
    public async Task ProcessPendingUpsertsExistingMessageWithoutDuplicating()
    {
        await using var db = CreateDb();
        SeedAccount(db, buyerId: 1, accountId: 1, provider: "Fake");
        db.EmailMessages.Add(new EmailMessage
        {
            BuyerId = 1, EmailAccountId = 1, ProviderMessageId = "m-1",
            Subject = "old", Folder = MailFolder.Junk
        });
        db.SyncJobs.Add(new SyncJob { BuyerId = 1, Status = SyncJobStatus.Pending });
        await db.SaveChangesAsync();

        var processor = CreateProcessor(new FakeProvider("Fake",
        [
            Message("m-1", MailFolder.Inbox, subject: "new")
        ]));

        await processor.ProcessPendingAsync(db, DateTimeOffset.UtcNow, CancellationToken.None);

        var stored = Assert.Single(await db.EmailMessages.ToListAsync());
        Assert.Equal("new", stored.Subject);
        Assert.Equal(MailFolder.Inbox, stored.Folder);
    }

    [Fact]
    public async Task ProcessPendingMarksJobFailedWhenProviderThrows()
    {
        await using var db = CreateDb();
        SeedAccount(db, buyerId: 1, accountId: 1, provider: "Boom");
        db.SyncJobs.Add(new SyncJob { BuyerId = 1, Status = SyncJobStatus.Pending });
        await db.SaveChangesAsync();

        var processor = CreateProcessor(new ThrowingProvider("Boom"));

        await processor.ProcessPendingAsync(db, DateTimeOffset.UtcNow, CancellationToken.None);

        var job = await db.SyncJobs.SingleAsync();
        Assert.Equal(SyncJobStatus.Failed, job.Status);
        Assert.Equal("boom", job.Error);
    }

    private static MailSyncProcessor CreateProcessor(IEmailProvider provider)
    {
        var resolver = new EmailProviderResolver([provider]);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MailSync:InitialSyncDays"] = "30" })
            .Build();
        return new MailSyncProcessor(resolver, config);
    }

    private static void SeedAccount(WebMailDbContext db, long buyerId, long accountId, string provider)
    {
        db.AllowedSenders.Add(new AllowedSender { EmailAddress = "orders@example.com" });
        db.EmailAccounts.Add(new EmailAccount
        {
            Id = accountId, BuyerId = buyerId, Email = "buyer@example.com",
            Provider = provider, ProviderUserId = "p", EncryptedRefreshToken = "token"
        });
    }

    private static ProviderMessage Message(string id, MailFolder folder, string subject = "s") =>
        new(id, null, "orders@example.com", "buyer@example.com", subject, DateTimeOffset.UtcNow, "body", null, null, folder);

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class FakeProvider(string name, IReadOnlyList<ProviderMessage> messages) : IEmailProvider
    {
        public string Name { get; } = name;
        public OAuthStartResult BuildAuthorizationUrl(string cardNo) => new(cardNo, cardNo);
        public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken) => Task.FromResult(messages);
    }

    private sealed class ThrowingProvider(string name) : IEmailProvider
    {
        public string Name { get; } = name;
        public OAuthStartResult BuildAuthorizationUrl(string cardNo) => new(cardNo, cardNo);
        public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken) => throw new InvalidOperationException("boom");
    }
}
