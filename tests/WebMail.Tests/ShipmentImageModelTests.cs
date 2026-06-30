using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using ImageModel = WebMail.Pages.Shipments.ImageModel;
using Xunit;

namespace WebMail.Tests;

public sealed class ShipmentImageModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shipimg_" + Guid.NewGuid().ToString("N"));

    private WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private ShipmentService Svc(WebMailDbContext db) => new(db, new SnowflakeIdGenerator(), _root);

    private static ImageModel CreateModel(WebMailDbContext db, ShipmentService svc, long userId, string role)
    {
        var model = new ImageModel(db, svc);
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, role) }, "test"));
        model.PageContext = new PageContext { HttpContext = new DefaultHttpContext { User = user } };
        return model;
    }

    private async Task<long> SeedShipment(WebMailDbContext db, ShipmentService svc, long buyerId)
    {
        await svc.CreateAsync(buyerId, "x",
            new ShipmentImageInput(new MemoryStream(new byte[] { 1, 2, 3 }), "image/png", 3), userId: 1);
        return (await db.Shipments.SingleAsync(s => s.BuyerId == buyerId)).Id;
    }

    [Fact]
    public async Task AdminGetsAnyImage()
    {
        await using var db = CreateDb();
        var svc = Svc(db);
        var id = await SeedShipment(db, svc, buyerId: 50);
        var model = CreateModel(db, svc, userId: 1, role: "Administrator");

        var result = await model.OnGetAsync(id);

        Assert.IsType<PhysicalFileResult>(result);
    }

    [Fact]
    public async Task AssignedSupplierGetsImage()
    {
        await using var db = CreateDb();
        var svc = Svc(db);
        var id = await SeedShipment(db, svc, buyerId: 50);
        db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = 50, SupplierId = 7 });
        await db.SaveChangesAsync();
        var model = CreateModel(db, svc, userId: 7, role: "Supplier");

        var result = await model.OnGetAsync(id);

        Assert.IsType<PhysicalFileResult>(result);
    }

    [Fact]
    public async Task UnassignedSupplierForbidden()
    {
        await using var db = CreateDb();
        var svc = Svc(db);
        var id = await SeedShipment(db, svc, buyerId: 50);
        var model = CreateModel(db, svc, userId: 8, role: "Supplier");

        var result = await model.OnGetAsync(id);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task MissingShipmentNotFound()
    {
        await using var db = CreateDb();
        var svc = Svc(db);
        var model = CreateModel(db, svc, userId: 1, role: "Administrator");

        var result = await model.OnGetAsync(999999);

        Assert.IsType<NotFoundResult>(result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
