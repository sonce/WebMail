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
        var buyer = new Buyer { CardNo = "card-2", EmailStatus = EmailAuthorizationStatus.Normal };
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
    public async Task UnlinkKeepsMessagesAuditableByBuyer()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "card-3", EmailStatus = EmailAuthorizationStatus.PendingReview };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        var account = new EmailAccount { BuyerId = buyer.Id, Email = "buyer@example.com", Provider = "Gmail", ProviderUserId = "provider-user", EncryptedRefreshToken = "token" };
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();
        db.EmailMessages.Add(new EmailMessage { BuyerId = buyer.Id, EmailAccountId = account.Id, ProviderMessageId = "m-1", Sender = "sender@example.com", Subject = "audit", SentAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var page = new EmailModel(db, new BuyerRuleService());

        await page.OnPostUnlinkAsync("card-3");

        Assert.Empty(await db.EmailAccounts.Where(x => x.BuyerId == buyer.Id).ToListAsync());
        Assert.Single(await db.EmailMessages.Where(x => x.BuyerId == buyer.Id).ToListAsync());
    }

    private static WebMailDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new WebMailDbContext(options);
    }
}
