using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using AdminBuyers = WebMail.Pages.Admin.BuyersModel;
using Xunit;

namespace WebMail.Tests;

public sealed class AdminBuyersModelTests
{
    [Fact]
    public async Task ApproveMovesPendingToApprovedWithAudit()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Pending, EmailStatus = EmailAuthorizationStatus.Authorized };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostApproveAsync(buyer.Id);

        Assert.Equal(ReviewStatus.Approved, (await db.Buyers.SingleAsync(x => x.Id == buyer.Id)).ReviewStatus);
        Assert.Single(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task RejectMovesPendingToRejected()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c2", Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Pending, EmailStatus = EmailAuthorizationStatus.Authorized };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostRejectAsync(buyer.Id);

        Assert.Equal(ReviewStatus.Rejected, (await db.Buyers.SingleAsync(x => x.Id == buyer.Id)).ReviewStatus);
    }

    [Fact]
    public async Task ApproveIgnoredWhenNotPending()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c3", Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized };
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
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Approved };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostDeleteAsync(buyer.Id);

        Assert.True((await db.Buyers.SingleAsync(x => x.Id == buyer.Id)).IsDeleted);
        Assert.Single(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task GetFiltersByReviewStatus()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "p", Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Pending });
        db.Buyers.Add(new Buyer { CardNo = "a", Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Approved });
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        model.ReviewFilter = ReviewStatus.Approved;
        await model.OnGetAsync();

        Assert.Equal("a", Assert.Single(model.Buyers).CardNo);
    }

    [Fact]
    public async Task GetFiltersByCardNoSubstring()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "ABC123", Stage = BuyerStage.Sent });
        db.Buyers.Add(new Buyer { CardNo = "XYZ999", Stage = BuyerStage.Sent });
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        model.CardNo = "ABC";
        await model.OnGetAsync();

        Assert.Equal("ABC123", Assert.Single(model.Buyers).CardNo);
    }

    [Fact]
    public async Task GetExcludesNotSentCards()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { CardNo = "sent", Stage = BuyerStage.Sent });
        db.Buyers.Add(new Buyer { CardNo = "notsent", Stage = BuyerStage.NotSent });
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnGetAsync();

        Assert.Equal("sent", Assert.Single(model.Buyers).CardNo);
    }

    [Fact]
    public async Task AssignSupplierCreatesAssignmentWithAudit()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Approved };
        var supplier = new AppUser { UserName = "sup", DisplayName = "Sup One", Role = UserRole.Supplier, IsActive = true };
        db.Buyers.Add(buyer);
        db.Users.Add(supplier);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostAssignSupplierAsync(buyer.Id, supplier.Id);

        var assignment = await db.BuyerSupplierAssignments.SingleOrDefaultAsync(x => x.BuyerId == buyer.Id);
        Assert.NotNull(assignment);
        Assert.Equal(supplier.Id, assignment!.SupplierId);
        var audit = Assert.Single(await db.AuditLogs.ToListAsync());
        Assert.Equal("AdminAssignSupplier", audit.Action);
    }

    [Fact]
    public async Task AssignSupplierUpdatesExistingRow()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted };
        var s1 = new AppUser { UserName = "s1", DisplayName = "S1", Role = UserRole.Supplier, IsActive = true };
        var s2 = new AppUser { UserName = "s2", DisplayName = "S2", Role = UserRole.Supplier, IsActive = true };
        db.Buyers.Add(buyer);
        db.Users.AddRange(s1, s2);
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = buyer.Id, SupplierId = s1.Id });
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostAssignSupplierAsync(buyer.Id, s2.Id);

        var rows = await db.BuyerSupplierAssignments.Where(x => x.BuyerId == buyer.Id).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(s2.Id, row.SupplierId);
    }

    [Fact]
    public async Task AssignSupplierNullRemovesAssignment()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted };
        var s1 = new AppUser { UserName = "s1", DisplayName = "S1", Role = UserRole.Supplier, IsActive = true };
        db.Buyers.Add(buyer);
        db.Users.Add(s1);
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = buyer.Id, SupplierId = s1.Id });
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostAssignSupplierAsync(buyer.Id, null);

        Assert.Empty(await db.BuyerSupplierAssignments.Where(x => x.BuyerId == buyer.Id).ToListAsync());
    }

    [Fact]
    public async Task AssignSupplierNullWithNoRowIsNoop()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostAssignSupplierAsync(buyer.Id, null);

        Assert.Empty(await db.BuyerSupplierAssignments.ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task AssignSupplierRejectsNonSupplierUser()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted };
        var sales = new AppUser { UserName = "sale", DisplayName = "Sale", Role = UserRole.Sales, IsActive = true };
        db.Buyers.Add(buyer);
        db.Users.Add(sales);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostAssignSupplierAsync(buyer.Id, sales.Id);

        Assert.Empty(await db.BuyerSupplierAssignments.ToListAsync());
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task AssignSupplierRejectsInactiveSupplier()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted };
        var sup = new AppUser { UserName = "sup", DisplayName = "Sup", Role = UserRole.Supplier, IsActive = false };
        db.Buyers.Add(buyer);
        db.Users.Add(sup);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostAssignSupplierAsync(buyer.Id, sup.Id);

        Assert.Empty(await db.BuyerSupplierAssignments.ToListAsync());
    }

    [Fact]
    public async Task AssignSupplierRejectsDeletedBuyer()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted, IsDeleted = true };
        var sup = new AppUser { UserName = "sup", DisplayName = "Sup", Role = UserRole.Supplier, IsActive = true };
        db.Buyers.Add(buyer);
        db.Users.Add(sup);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostAssignSupplierAsync(buyer.Id, sup.Id);

        Assert.Empty(await db.BuyerSupplierAssignments.ToListAsync());
    }

    [Fact]
    public async Task GetLoadsSuppliersAndAssignmentMap()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted };
        var sup = new AppUser { UserName = "sup", DisplayName = "Sup One", Role = UserRole.Supplier, IsActive = true };
        var inactive = new AppUser { UserName = "dead", DisplayName = "Dead", Role = UserRole.Supplier, IsActive = false };
        db.Buyers.Add(buyer);
        db.Users.AddRange(sup, inactive);
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = buyer.Id, SupplierId = sup.Id });
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnGetAsync();

        Assert.Equal("Sup One", Assert.Single(model.Suppliers).DisplayName);
        Assert.True(model.AssignmentByBuyer.TryGetValue(buyer.Id, out var view));
        Assert.Equal(sup.Id, view.SupplierId);
        Assert.Equal("Sup One", view.DisplayName);
    }

    [Fact]
    public async Task GetAssignmentMapShowsUnassignedAsNull()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnGetAsync();

        Assert.True(model.AssignmentByBuyer.TryGetValue(buyer.Id, out var view));
        Assert.Null(view.SupplierId);
        Assert.Equal(string.Empty, view.DisplayName);
    }

    [Fact]
    public async Task SetStatusMovesApprovedBuyerToCompletedWithAudit()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostSetStatusAsync(buyer.Id, SupplierProcessingStatus.Completed);

        Assert.Equal(SupplierProcessingStatus.Completed, (await db.Buyers.SingleAsync(x => x.Id == buyer.Id)).SupplierStatus);
        var audit = Assert.Single(await db.AuditLogs.ToListAsync());
        Assert.Equal("AdminSetStatus", audit.Action);
    }

    [Fact]
    public async Task SetStatusIgnoredWhenNotApproved()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Pending };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostSetStatusAsync(buyer.Id, SupplierProcessingStatus.Completed);

        Assert.Equal(SupplierProcessingStatus.Unprocessed, (await db.Buyers.SingleAsync(x => x.Id == buyer.Id)).SupplierStatus);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task SetStatusRejectsInvalidStatus()
    {
        await using var db = CreateDb();
        var buyer = new Buyer { CardNo = "c1", Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Approved };
        db.Buyers.Add(buyer);
        await db.SaveChangesAsync();

        var model = CreateModel(db, adminId: 1);
        await model.OnPostSetStatusAsync(buyer.Id, (SupplierProcessingStatus)999);

        Assert.Equal(SupplierProcessingStatus.Unprocessed, (await db.Buyers.SingleAsync(x => x.Id == buyer.Id)).SupplierStatus);
        Assert.Empty(await db.AuditLogs.ToListAsync());
        Assert.NotNull(model.Message);
    }

    private static AdminBuyers CreateModel(WebMailDbContext db, long adminId)
    {
        var model = new AdminBuyers(db, new BuyerReviewService(db), TestLocalizer.Shared);
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
