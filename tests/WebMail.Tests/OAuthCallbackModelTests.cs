using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
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
    public async Task NewBindingGoesToAuthorizedPendingReviewAndEncryptsToken()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "c1", Stage = BuyerStage.NotSubmitted });
        await db.SaveChangesAsync();

        var store = new FakeOAuthStateStore();
        var state = store.Issue("Gmail", "c1");
        var model = CreateModel(db, new FakeAuthProvider("Gmail", "new@example.com"), store);
        await model.OnGetAsync("code", state, null, CancellationToken.None);

        var buyer = await db.Buyers.SingleAsync(x => x.CardNo == "c1");
        Assert.Equal(EmailAuthorizationStatus.Authorized, buyer.EmailStatus);
        Assert.Equal(ReviewStatus.Pending, buyer.ReviewStatus);
        Assert.Equal(BuyerStage.Submitted, buyer.Stage);
        var account = Assert.Single(await db.EmailAccounts.Where(x => x.BuyerId == buyer.Id).ToListAsync());
        Assert.Equal("enc:refresh", account.EncryptedRefreshToken);
    }

    [Fact]
    public async Task ForgedStateIsRejectedAndNothingIsBound()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "c1", Stage = BuyerStage.NotSubmitted });
        await db.SaveChangesAsync();

        var model = CreateModel(db, new FakeAuthProvider("Gmail", "new@example.com"), new FakeOAuthStateStore());
        await model.OnGetAsync("code", "forged-state", null, CancellationToken.None);

        Assert.NotNull(model.ErrorMessage);
        Assert.Empty(await db.EmailAccounts.ToListAsync());
        var buyer = await db.Buyers.SingleAsync(x => x.CardNo == "c1");
        Assert.Equal(EmailAuthorizationStatus.NotAuthorized, buyer.EmailStatus);
    }

    [Fact]
    public async Task FirstAuthorizationStampsCardUsedAt()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "c1", Stage = BuyerStage.NotSubmitted });
        await db.SaveChangesAsync();

        var store = new FakeOAuthStateStore();
        var state = store.Issue("Gmail", "c1");
        var model = CreateModel(db, new FakeAuthProvider("Gmail", "new@example.com"), store);
        await model.OnGetAsync("code", state, null, CancellationToken.None);

        var buyer = await db.Buyers.SingleAsync(x => x.CardNo == "c1");
        Assert.NotNull(buyer.CardUsedAt);
    }

    [Fact]
    public async Task ReauthorizationDoesNotChangeCardUsedAt()
    {
        await using var db = CreateDb();
        var firstUsed = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var buyer = new Buyer { CardNo = "c2", Stage = BuyerStage.Submitted, EmailStatus = EmailAuthorizationStatus.Abnormal, CardUsedAt = firstUsed };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        db.EmailAccounts.Add(new EmailAccount { BuyerId = buyer.Id, Provider = "Gmail", Email = "same@example.com", ProviderUserId = "u", EncryptedRefreshToken = "enc:old" });
        await db.SaveChangesAsync();

        var store = new FakeOAuthStateStore();
        var state = store.Issue("Gmail", "c2");
        var model = CreateModel(db, new FakeAuthProvider("Gmail", "same@example.com"), store);
        await model.OnGetAsync("code", state, null, CancellationToken.None);

        Assert.Equal(firstUsed, (await db.Buyers.SingleAsync(x => x.Id == buyer.Id)).CardUsedAt);
    }

    [Fact]
    public async Task AbnormalSameMailboxRecoversWithoutTouchingReviewOrSupplier()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c2", Stage = BuyerStage.Submitted, EmailStatus = EmailAuthorizationStatus.Abnormal, ReviewStatus = ReviewStatus.Approved, SupplierStatus = SupplierProcessingStatus.Failed };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        db.EmailAccounts.Add(new EmailAccount { BuyerId = buyer.Id, Provider = "Gmail", Email = "same@example.com", ProviderUserId = "u", EncryptedRefreshToken = "enc:old" });
        await db.SaveChangesAsync();

        var store = new FakeOAuthStateStore();
        var state = store.Issue("Gmail", "c2");
        var model = CreateModel(db, new FakeAuthProvider("Gmail", "same@example.com"), store);
        await model.OnGetAsync("code", state, null, CancellationToken.None);

        var reloaded = await db.Buyers.SingleAsync(x => x.Id == buyer.Id);
        Assert.Equal(EmailAuthorizationStatus.Authorized, reloaded.EmailStatus);
        Assert.Equal(ReviewStatus.Approved, reloaded.ReviewStatus);
        Assert.Equal(SupplierProcessingStatus.Failed, reloaded.SupplierStatus);
    }

    [Fact]
    public async Task ChangedAccountBlockedWhenLocked()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c3", Stage = BuyerStage.Submitted, EmailStatus = EmailAuthorizationStatus.Authorized, ReviewStatus = ReviewStatus.Approved, SupplierStatus = SupplierProcessingStatus.Unprocessed };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        db.EmailAccounts.Add(new EmailAccount { BuyerId = buyer.Id, Provider = "Gmail", Email = "old@example.com", ProviderUserId = "u", EncryptedRefreshToken = "enc:old" });
        await db.SaveChangesAsync();

        var store = new FakeOAuthStateStore();
        var state = store.Issue("Gmail", "c3");
        var model = CreateModel(db, new FakeAuthProvider("Gmail", "new@example.com"), store);
        await model.OnGetAsync("code", state, null, CancellationToken.None);

        var account = await db.EmailAccounts.SingleAsync(x => x.BuyerId == buyer.Id);
        Assert.Equal("old@example.com", account.Email);
        var reloaded = await db.Buyers.SingleAsync(x => x.Id == buyer.Id);
        Assert.Equal(EmailAuthorizationStatus.Authorized, reloaded.EmailStatus);
        Assert.Equal(ReviewStatus.Approved, reloaded.ReviewStatus);
        Assert.Equal(BuyerStage.Submitted, reloaded.Stage);
        Assert.Equal(SupplierProcessingStatus.Unprocessed, reloaded.SupplierStatus);
        Assert.NotNull(model.ErrorMessage);
    }

    [Fact]
    public async Task AutoApproveCardGoesStraightToApprovedOnNewBinding()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "ca", Stage = BuyerStage.NotSubmitted, AutoApprove = true });
        await db.SaveChangesAsync();

        var store = new FakeOAuthStateStore();
        var state = store.Issue("Gmail", "ca");
        var model = CreateModel(db, new FakeAuthProvider("Gmail", "new@example.com"), store);
        await model.OnGetAsync("code", state, null, CancellationToken.None);

        var buyer = await db.Buyers.SingleAsync(x => x.CardNo == "ca");
        Assert.Equal(BuyerStage.Submitted, buyer.Stage);
        Assert.Equal(EmailAuthorizationStatus.Authorized, buyer.EmailStatus);
        Assert.Equal(ReviewStatus.Approved, buyer.ReviewStatus);
    }

    [Fact]
    public async Task NonAutoApproveCardStaysPendingOnNewBinding()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "cn", Stage = BuyerStage.NotSubmitted, AutoApprove = false });
        await db.SaveChangesAsync();

        var store = new FakeOAuthStateStore();
        var state = store.Issue("Gmail", "cn");
        var model = CreateModel(db, new FakeAuthProvider("Gmail", "new@example.com"), store);
        await model.OnGetAsync("code", state, null, CancellationToken.None);

        var buyer = await db.Buyers.SingleAsync(x => x.CardNo == "cn");
        Assert.Equal(ReviewStatus.Pending, buyer.ReviewStatus);
    }

    private static CallbackModel CreateModel(WebMailDbContext db, IEmailProvider provider, FakeOAuthStateStore store)
    {
        var model = new CallbackModel(db, new BuyerRuleService(), new EmailProviderResolver([provider]), TestLocalizer.Shared, store, new FakeTokenProtector());
        model.PageContext = new PageContext { HttpContext = new DefaultHttpContext { Request = { Scheme = "https", Host = new HostString("localhost", 7121) } } };
        return model;
    }

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private sealed class FakeAuthProvider(string name, string email) : IEmailProvider
    {
        public string Name { get; } = name;
        public OAuthStartResult BuildAuthorizationUrl(string state, string redirectUri) => new(state, state);
        public Task<OAuthCallbackResult> CompleteAuthorizationAsync(string code, string state, string redirectUri, CancellationToken cancellationToken) =>
            Task.FromResult(new OAuthCallbackResult(email, "provider-user", "refresh"));
        public Task<IReadOnlyList<ProviderMessage>> FetchMessagesAsync(string refreshToken, IReadOnlyCollection<string> allowedSenders, DateTimeOffset? since, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
