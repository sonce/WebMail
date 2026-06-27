using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using AdminBuyers = WebMail.Pages.Admin.BuyersModel;
using Xunit;

namespace WebMail.Tests;

public sealed class AdminBuyersModelTests
{
    [Fact]
    public async Task ApproveMovesPendingToApprovedWithAudit()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", BuyerStatus = BuyerStatus.PendingReview, EmailStatus = EmailAuthorizationStatus.Authorized };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostApproveAsync(buyer.Id);

        Assert.Equal(BuyerStatus.Approved, (await db.Buyers.SingleAsync(x => x.Id == buyer.Id)).BuyerStatus);
        Assert.Single(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task RejectMovesPendingToRejected()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c2", BuyerStatus = BuyerStatus.PendingReview, EmailStatus = EmailAuthorizationStatus.Authorized };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostRejectAsync(buyer.Id);

        Assert.Equal(BuyerStatus.Rejected, (await db.Buyers.SingleAsync(x => x.Id == buyer.Id)).BuyerStatus);
    }

    [Fact]
    public async Task ApproveIgnoredWhenNotPending()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c3", BuyerStatus = BuyerStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostApproveAsync(buyer.Id);

        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task DeleteSoftDeletesBuyerWithAudit()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", BuyerStatus = BuyerStatus.Approved };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostDeleteAsync(buyer.Id);

        Assert.True((await db.Buyers.SingleAsync(x => x.Id == buyer.Id)).IsDeleted);
        Assert.Single(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task GetFiltersByBuyerStatus()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "p", BuyerStatus = BuyerStatus.PendingReview });
        db.Buyers.Add(new Buyer { CardNo = "a", BuyerStatus = BuyerStatus.Approved });
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        model.StatusFilter = BuyerStatus.Approved;
        await model.OnGetAsync();

        Assert.Equal("a", Assert.Single(model.Buyers).CardNo);
    }

    [Fact]
    public async Task GetFiltersByCardNoSubstring()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "ABC123" });
        db.Buyers.Add(new Buyer { CardNo = "XYZ999" });
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        model.CardNo = "ABC";
        await model.OnGetAsync();

        Assert.Equal("ABC123", Assert.Single(model.Buyers).CardNo);
    }

    private static AdminBuyers CreateModel(WebMailDbContext db, long adminId)
    {
        var model = new AdminBuyers(db);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, adminId.ToString())], "test"))
        };
        model.PageContext = new PageContext { HttpContext = httpContext };
        return model;
    }

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
