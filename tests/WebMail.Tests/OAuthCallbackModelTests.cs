using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Pages.OAuth;
using WebMail.Services;
using WebMail.Services.EmailProviders;
using Xunit;

namespace WebMail.Tests;

public sealed class OAuthCallbackModelTests
{
    [Fact]
    public async Task NewBindingGoesToAuthorizedPendingReview()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "c1", CardStatus = CardStatus.Entered });
        await db.SaveChangesAsync();

        var model = CreateModel(db, new FakeAuthProvider("Gmail", "new@example.com"));
        await model.OnGetAsync("Gmail", "code", "c1", null, CancellationToken.None);

        var buyer = await db.Buyers.SingleAsync(x => x.CardNo == "c1");
        Assert.Equal(EmailAuthorizationStatus.Authorized, buyer.EmailStatus);
        Assert.Equal(BuyerStatus.PendingReview, buyer.BuyerStatus);
        Assert.Single(await db.EmailAccounts.Where(x => x.BuyerId == buyer.Id).ToListAsync());
    }

    [Fact]
    public async Task AbnormalSameMailboxRecoversWithoutTouchingReviewOrSupplier()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c2", CardStatus = CardStatus.Authorized, EmailStatus = EmailAuthorizationStatus.Abnormal, BuyerStatus = BuyerStatus.Approved, SupplierStatus = SupplierProcessingStatus.Failed };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        db.EmailAccounts.Add(new EmailAccount { BuyerId = buyer.Id, Provider = "Gmail", Email = "same@example.com", ProviderUserId = "u", EncryptedRefreshToken = "old" });
        await db.SaveChangesAsync();

        var model = CreateModel(db, new FakeAuthProvider("Gmail", "same@example.com"));
        await model.OnGetAsync("Gmail", "code", "c2", null, CancellationToken.None);

        var reloaded = await db.Buyers.SingleAsync(x => x.Id == buyer.Id);
        Assert.Equal(EmailAuthorizationStatus.Authorized, reloaded.EmailStatus);
        Assert.Equal(BuyerStatus.Approved, reloaded.BuyerStatus);
        Assert.Equal(SupplierProcessingStatus.Failed, reloaded.SupplierStatus);
    }

    [Fact]
    public async Task ChangedAccountBlockedWhenLocked()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c3", CardStatus = CardStatus.Authorized, EmailStatus = EmailAuthorizationStatus.Authorized, BuyerStatus = BuyerStatus.Approved, SupplierStatus = SupplierProcessingStatus.Unprocessed };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        db.EmailAccounts.Add(new EmailAccount { BuyerId = buyer.Id, Provider = "Gmail", Email = "old@example.com", ProviderUserId = "u", EncryptedRefreshToken = "old" });
        await db.SaveChangesAsync();

        var model = CreateModel(db, new FakeAuthProvider("Gmail", "new@example.com"));
        await model.OnGetAsync("Gmail", "code", "c3", null, CancellationToken.None);

        var account = await db.EmailAccounts.SingleAsync(x => x.BuyerId == buyer.Id);
        Assert.Equal("old@example.com", account.Email);
        var reloaded = await db.Buyers.SingleAsync(x => x.Id == buyer.Id);
        Assert.Equal(EmailAuthorizationStatus.Authorized, reloaded.EmailStatus);
        Assert.Equal(BuyerStatus.Approved, reloaded.BuyerStatus);
        Assert.Equal(SupplierProcessingStatus.Unprocessed, reloaded.SupplierStatus);
        Assert.NotNull(model.ErrorMessage);
    }

    private static CallbackModel CreateModel(WebMailDbContext db, IEmailProvider provider) =>
        new(db, new BuyerRuleService(), new EmailProviderResolver([provider]));

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class FakeAuthProvider(string name, string email) : IEmailProvider
    {
        public string Name { get; } = name;
        public OAuthStartResult BuildAuthorizationUrl(string cardNo) => new(cardNo, cardNo);
        public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, CancellationToken cancellationToken) =>
            Task.FromResult(new OAuthCallbackResult(email, "provider-user", "refresh"));
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
