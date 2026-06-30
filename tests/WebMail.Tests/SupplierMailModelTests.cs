using System;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using MailModel = WebMail.Pages.Supplier.MailModel;
using Xunit;

namespace WebMail.Tests;

public sealed class SupplierMailModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shipmail_" + Guid.NewGuid().ToString("N"));

    private WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private ShipmentService Svc(WebMailDbContext db) => new(db, new SnowflakeIdGenerator(), _root);

    private MailModel CreateModel(WebMailDbContext db, long userId, string role)
    {
        var model = new MailModel(db, Svc(db), TestLocalizer.Shared);
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, role) }, "test"));
        model.PageContext = new PageContext { HttpContext = new DefaultHttpContext { User = user } };
        return model;
    }

    private static IFormFile Png()
    {
        var bytes = Encoding.ASCII.GetBytes("pngdata");
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "image", "a.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };
    }

    private static Buyer SeedApprovedBuyer(WebMailDbContext db, long id) => new()
    {
        Id = id, CardNo = "c" + id, Stage = BuyerStage.Submitted,
        ReviewStatus = ReviewStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized
    };

    [Fact]
    public async Task AdminCanAddShipmentForAnyBuyer()
    {
        await using var db = CreateDb();
        db.Buyers.Add(SeedApprovedBuyer(db, 40));
        await db.SaveChangesAsync();
        var model = CreateModel(db, userId: 1, role: "Administrator");

        var result = await model.OnPostAddShipmentAsync(buyerId: 40, description: "d", image: Png());

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Single(await db.Shipments.Where(s => s.BuyerId == 40).ToListAsync());
    }

    [Fact]
    public async Task AssignedSupplierCanAddShipment()
    {
        await using var db = CreateDb();
        db.Buyers.Add(SeedApprovedBuyer(db, 41));
        await db.SaveChangesAsync();
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = 41, SupplierId = 7 });
        await db.SaveChangesAsync();
        var model = CreateModel(db, userId: 7, role: "Supplier");

        var result = await model.OnPostAddShipmentAsync(41, "d", Png());

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Single(await db.Shipments.Where(s => s.BuyerId == 41).ToListAsync());
    }

    [Fact]
    public async Task AssignedSupplierCannotAddShipmentForNotReadyBuyer()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer
        {
            Id = 44, CardNo = "c44", Stage = BuyerStage.Submitted,
            ReviewStatus = ReviewStatus.Pending, EmailStatus = EmailAuthorizationStatus.Authorized
        });
        await db.SaveChangesAsync();
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = 44, SupplierId = 7 });
        await db.SaveChangesAsync();
        var model = CreateModel(db, userId: 7, role: "Supplier");

        var result = await model.OnPostAddShipmentAsync(44, "d", Png());

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(await db.Shipments.Where(s => s.BuyerId == 44).ToListAsync());
    }

    [Fact]
    public async Task UnassignedSupplierCannotAddShipment()
    {
        await using var db = CreateDb();
        db.Buyers.Add(SeedApprovedBuyer(db, 42));
        await db.SaveChangesAsync();
        var model = CreateModel(db, userId: 8, role: "Supplier");

        var result = await model.OnPostAddShipmentAsync(42, "d", Png());

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(await db.Shipments.ToListAsync());
    }

    [Fact]
    public async Task SupplierCannotDeleteOtherBuyersShipment()
    {
        await using var db = CreateDb();
        db.Buyers.Add(SeedApprovedBuyer(db, 43));
        await db.SaveChangesAsync();
        var svc = Svc(db);
        await svc.CreateAsync(43, "d", new ShipmentImageInput(new MemoryStream(new byte[]{1}), "image/png", 1), userId: 1);
        var shipmentId = (await db.Shipments.SingleAsync()).Id;
        var model = CreateModel(db, userId: 8, role: "Supplier"); // not assigned to buyer 43

        var result = await model.OnPostDeleteShipmentAsync(shipmentId, buyerId: 43);

        Assert.IsType<ForbidResult>(result);
        Assert.Single(await db.Shipments.ToListAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
