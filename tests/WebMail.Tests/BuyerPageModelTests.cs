using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Pages.Buyer;
using WebMail.Services;

namespace WebMail.Tests;

public sealed class BuyerPageModelTests
{
    [Fact]
    public async Task VerifyDoesNotTrustSaleIdFromPublicRequest()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "card-1" });
        await db.SaveChangesAsync();

        var page = new VerifyModel(db);

        await page.OnGetAsync("card-1", 99);

        var buyer = await db.Buyers.SingleAsync(x => x.CardNo == "card-1");
        Assert.Null(buyer.SaleId);
    }

    [Fact]
    public async Task BuyerEmailPageDoesNotLoadStoredMessages()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "card-2", BuyerStatus = BuyerStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        var account = new EmailAccount { BuyerId = buyer.Id, Email = "buyer@example.com", Provider = "Gmail", ProviderUserId = "provider-user", EncryptedRefreshToken = "token" };
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();
        db.EmailMessages.Add(new EmailMessage { BuyerId = buyer.Id, EmailAccountId = account.Id, ProviderMessageId = "m-1", Sender = "sender@example.com", Subject = "private", SentAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var page = new EmailModel(db, new BuyerRuleService());

        await page.OnGetAsync("card-2");

        Assert.Empty(page.Messages);
    }

    [Fact]
    public async Task ChangeEmailClearsBindingAndResetsToFreshCycle()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "card-3", BuyerStatus = BuyerStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized, SupplierStatus = SupplierProcessingStatus.Failed };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        var account = new EmailAccount { BuyerId = buyer.Id, Email = "buyer@example.com", Provider = "Gmail", ProviderUserId = "u", EncryptedRefreshToken = "token" };
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();
        db.EmailMessages.Add(new EmailMessage { BuyerId = buyer.Id, EmailAccountId = account.Id, ProviderMessageId = "m-1", Sender = "s@example.com", Subject = "audit", SentAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var page = new EmailModel(db, new BuyerRuleService());
        await page.OnPostChangeEmailAsync("card-3");

        var reloaded = await db.Buyers.SingleAsync(x => x.Id == buyer.Id);
        Assert.Equal(EmailAuthorizationStatus.NotAuthorized, reloaded.EmailStatus);
        Assert.Equal(BuyerStatus.NotSubmitted, reloaded.BuyerStatus);
        Assert.Equal(SupplierProcessingStatus.Unprocessed, reloaded.SupplierStatus);
        Assert.Empty(await db.EmailAccounts.Where(x => x.BuyerId == buyer.Id).ToListAsync());
        Assert.Single(await db.EmailMessages.Where(x => x.BuyerId == buyer.Id).ToListAsync());
    }

    [Fact]
    public async Task ClearAuthFromCompletedIsTerminal()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "card-4", BuyerStatus = BuyerStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized, SupplierStatus = SupplierProcessingStatus.Completed };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        db.EmailAccounts.Add(new EmailAccount { BuyerId = buyer.Id, Email = "buyer@example.com", Provider = "Gmail", ProviderUserId = "u", EncryptedRefreshToken = "token" });
        await db.SaveChangesAsync();

        var page = new EmailModel(db, new BuyerRuleService());
        await page.OnPostClearAuthAsync("card-4");

        var reloaded = await db.Buyers.SingleAsync(x => x.Id == buyer.Id);
        Assert.Equal(EmailAuthorizationStatus.NotAuthorized, reloaded.EmailStatus);
        Assert.Equal(BuyerStatus.Approved, reloaded.BuyerStatus);
        Assert.Equal(SupplierProcessingStatus.Completed, reloaded.SupplierStatus);
        Assert.Equal(BuyerMailAction.None, new BuyerRuleService().ResolveBuyerMailAction(reloaded));
    }

    [Fact]
    public async Task ClearAuthBlockedWhileProcessing()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "card-5", BuyerStatus = BuyerStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized, SupplierStatus = SupplierProcessingStatus.Unprocessed };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        db.EmailAccounts.Add(new EmailAccount { BuyerId = buyer.Id, Email = "buyer@example.com", Provider = "Gmail", ProviderUserId = "u", EncryptedRefreshToken = "token" });
        await db.SaveChangesAsync();

        var page = new EmailModel(db, new BuyerRuleService());
        await page.OnPostClearAuthAsync("card-5");

        Assert.Single(await db.EmailAccounts.Where(x => x.BuyerId == buyer.Id).ToListAsync());
    }

    private static WebMailDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new WebMailDbContext(options);
    }
}
