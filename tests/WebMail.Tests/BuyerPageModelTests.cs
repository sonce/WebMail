using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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
        db.Buyers.Add(new Buyer { CardNo = "card-1", Stage = BuyerStage.Sent });
        await db.SaveChangesAsync();

        var page = new VerifyModel(db, TestLocalizer.Shared);

        await page.OnGetAsync("card-1", 99);

        var buyer = await db.Buyers.SingleAsync(x => x.CardNo == "card-1");
        Assert.Null(buyer.SaleId);
    }

    [Fact]
    public async Task VerifyAdmitsSentCardAndTransitionsToNotSubmitted()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "card-sent", Stage = BuyerStage.Sent });
        await db.SaveChangesAsync();

        var page = new VerifyModel(db, TestLocalizer.Shared);

        var result = await page.OnGetAsync("card-sent", null);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("Email", redirect.PageName);
        Assert.Equal(BuyerStage.NotSubmitted,
            (await db.Buyers.SingleAsync(x => x.CardNo == "card-sent")).Stage);
    }

    [Fact]
    public async Task VerifyBlocksUnsentCard()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "card-unsent", Stage = BuyerStage.NotSent });
        await db.SaveChangesAsync();

        var page = new VerifyModel(db, TestLocalizer.Shared);

        var result = await page.OnGetAsync("card-unsent", null);

        // 卡密未发送（Stage=NotSent）→ 链接不可用，提示「链接无效或已失效」。
        Assert.IsType<PageResult>(result);
        Assert.Equal("Buyer.LinkInvalidOrExpired", page.ErrorMessage);
        Assert.Equal(BuyerStage.NotSent,
            (await db.Buyers.SingleAsync(x => x.CardNo == "card-unsent")).Stage);
    }

    [Fact]
    public async Task VerifyRevisitsNotSubmittedWithoutAdvancing()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "card-2", Stage = BuyerStage.NotSubmitted });
        await db.SaveChangesAsync();

        var page = new VerifyModel(db, TestLocalizer.Shared);

        var result = await page.OnGetAsync("card-2", null);

        // 已进入(NotSubmitted) 回访 → 直接跳状态页，状态不重复推进。
        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("Email", redirect.PageName);
        Assert.Equal(BuyerStage.NotSubmitted,
            (await db.Buyers.SingleAsync(x => x.CardNo == "card-2")).Stage);
    }

    [Fact]
    public async Task VerifyRevisitsSubmittedWithoutAdvancing()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "card-submitted", Stage = BuyerStage.Submitted });
        await db.SaveChangesAsync();

        var page = new VerifyModel(db, TestLocalizer.Shared);

        var result = await page.OnGetAsync("card-submitted", null);

        // 已授权(Submitted) 回访 → 跳状态页查看 / 操作，状态不变。
        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("Email", redirect.PageName);
        Assert.Equal(BuyerStage.Submitted,
            (await db.Buyers.SingleAsync(x => x.CardNo == "card-submitted")).Stage);
    }

    [Fact]
    public async Task ChangeEmailClearsBindingAndResetsToFreshCycle()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "card-3", Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized, SupplierStatus = SupplierProcessingStatus.Failed };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        var account = new EmailAccount { BuyerId = buyer.Id, Email = "buyer@example.com", Provider = "Gmail", ProviderUserId = "u", EncryptedRefreshToken = "token" };
        db.EmailAccounts.Add(account);
        await db.SaveChangesAsync();

        var page = new EmailModel(db, new BuyerRuleService(), TestLocalizer.Shared);
        await page.OnPostChangeEmailAsync("card-3");

        var reloaded = await db.Buyers.SingleAsync(x => x.Id == buyer.Id);
        Assert.Equal(EmailAuthorizationStatus.NotAuthorized, reloaded.EmailStatus);
        Assert.Equal(BuyerStage.Opened, reloaded.Stage);
        Assert.Equal(ReviewStatus.Pending, reloaded.ReviewStatus);
        Assert.Equal(SupplierProcessingStatus.Unprocessed, reloaded.SupplierStatus);
        Assert.Empty(await db.EmailAccounts.Where(x => x.BuyerId == buyer.Id).ToListAsync());
    }

    [Fact]
    public async Task ClearAuthFromCompletedIsTerminal()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "card-4", Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized, SupplierStatus = SupplierProcessingStatus.Completed };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        db.EmailAccounts.Add(new EmailAccount { BuyerId = buyer.Id, Email = "buyer@example.com", Provider = "Gmail", ProviderUserId = "u", EncryptedRefreshToken = "token" });
        await db.SaveChangesAsync();

        var page = new EmailModel(db, new BuyerRuleService(), TestLocalizer.Shared);
        await page.OnPostClearAuthAsync("card-4");

        var reloaded = await db.Buyers.SingleAsync(x => x.Id == buyer.Id);
        Assert.Equal(EmailAuthorizationStatus.NotAuthorized, reloaded.EmailStatus);
        Assert.Equal(ReviewStatus.Approved, reloaded.ReviewStatus);
        Assert.Equal(SupplierProcessingStatus.Completed, reloaded.SupplierStatus);
        Assert.Equal(BuyerMailAction.None, new BuyerRuleService().ResolveBuyerMailAction(reloaded));
    }

    [Fact]
    public async Task ClearAuthBlockedWhileProcessing()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "card-5", Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized, SupplierStatus = SupplierProcessingStatus.Unprocessed };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        db.EmailAccounts.Add(new EmailAccount { BuyerId = buyer.Id, Email = "buyer@example.com", Provider = "Gmail", ProviderUserId = "u", EncryptedRefreshToken = "token" });
        await db.SaveChangesAsync();

        var page = new EmailModel(db, new BuyerRuleService(), TestLocalizer.Shared);
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
