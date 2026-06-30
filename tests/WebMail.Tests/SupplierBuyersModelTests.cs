using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using SupplierBuyers = WebMail.Pages.Supplier.BuyersModel;
using Xunit;

namespace WebMail.Tests;

public sealed class SupplierBuyersModelTests
{
    [Fact]
    public async Task SetStatusMarksFailedAndWritesAudit()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = buyer.Id, SupplierId = 3 });
        await db.SaveChangesAsync();

        var model = CreateModel(db, supplierId: 3);
        await model.OnPostSetStatusAsync(buyer.Id, SupplierProcessingStatus.Failed);

        var reloaded = await db.Buyers.SingleAsync(x => x.Id == buyer.Id);
        Assert.Equal(SupplierProcessingStatus.Failed, reloaded.SupplierStatus);
        Assert.Single(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task SetStatusBlockedWhenNotApproved()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c2", Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Pending, EmailStatus = EmailAuthorizationStatus.Authorized };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = buyer.Id, SupplierId = 3 });
        await db.SaveChangesAsync();

        var model = CreateModel(db, supplierId: 3);
        await model.OnPostSetStatusAsync(buyer.Id, SupplierProcessingStatus.Completed);

        Assert.Equal(SupplierProcessingStatus.Unprocessed, (await db.Buyers.SingleAsync(x => x.Id == buyer.Id)).SupplierStatus);
    }

    [Fact]
    public async Task SetStatusBlockedForOtherSupplier()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c3", Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = buyer.Id, SupplierId = 99 });
        await db.SaveChangesAsync();

        var model = CreateModel(db, supplierId: 3);
        await model.OnPostSetStatusAsync(buyer.Id, SupplierProcessingStatus.Failed);

        Assert.Equal(SupplierProcessingStatus.Unprocessed, (await db.Buyers.SingleAsync(x => x.Id == buyer.Id)).SupplierStatus);
    }

    private static SupplierBuyers CreateModel(WebMailDbContext db, long supplierId)
    {
        var model = new SupplierBuyers(db, new BuyerRuleService(), TestLocalizer.Shared);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, supplierId.ToString())], "test"))
        };
        model.PageContext = new PageContext { HttpContext = httpContext };
        return model;
    }

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
