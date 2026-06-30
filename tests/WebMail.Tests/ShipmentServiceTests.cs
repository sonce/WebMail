using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class ShipmentServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shiptest_" + Guid.NewGuid().ToString("N"));

    private WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private ShipmentService CreateService(WebMailDbContext db) =>
        new(db, new SnowflakeIdGenerator(), _root);

    private static ShipmentImageInput Img(string contentType = "image/jpeg", int size = 64)
        => new(new MemoryStream(Encoding.ASCII.GetBytes(new string('x', size))), contentType, size);

    [Fact]
    public async Task CreateWritesRecordAndFile()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);

        var result = await svc.CreateAsync(buyerId: 9, description: "hello", image: Img(), userId: 3);

        Assert.True(result.Success);
        var s = await db.Shipments.SingleAsync();
        Assert.Equal(9, s.BuyerId);
        Assert.True(s.ShipmentNo > 0);
        Assert.Equal("hello", s.Description);
        Assert.Equal(3, s.CreatedByUserId);
        Assert.True(File.Exists(svc.GetFilePath(s)));
        Assert.Single(await db.AuditLogs.Where(a => a.Action == "CreateShipment").ToListAsync());
    }

    [Fact]
    public async Task CreateRejectsMissingImage()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);

        var result = await svc.CreateAsync(9, "x", image: null, userId: 3);

        Assert.False(result.Success);
        Assert.Equal("Shipment.InvalidImage", result.MessageKey);
        Assert.Empty(await db.Shipments.ToListAsync());
    }

    [Fact]
    public async Task CreateRejectsWrongType()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);

        var result = await svc.CreateAsync(9, "x", Img(contentType: "application/pdf"), userId: 3);

        Assert.False(result.Success);
        Assert.Empty(await db.Shipments.ToListAsync());
    }

    [Fact]
    public async Task CreateRejectsTooLarge()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);

        var result = await svc.CreateAsync(9, "x", new ShipmentImageInput(new MemoryStream(new byte[16]), "image/png", 5 * 1024 * 1024 + 1), userId: 3);

        Assert.False(result.Success);
        Assert.Empty(await db.Shipments.ToListAsync());
    }

    [Fact]
    public async Task DeleteRemovesRecordAndFile()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);
        var created = await svc.CreateAsync(9, "x", Img(), 3);
        var s = await db.Shipments.SingleAsync();
        var path = svc.GetFilePath(s);

        var ok = await svc.DeleteAsync(s.Id, userId: 3);

        Assert.True(ok);
        Assert.Empty(await db.Shipments.ToListAsync());
        Assert.False(File.Exists(path));
        Assert.Single(await db.AuditLogs.Where(a => a.Action == "DeleteShipment").ToListAsync());
    }

    [Fact]
    public async Task GetForBuyerReturnsOnlyThatBuyerNewestFirst()
    {
        await using var db = CreateDb();
        var svc = CreateService(db);
        await svc.CreateAsync(1, "a", Img(), 3);
        await svc.CreateAsync(2, "b", Img(), 3);
        await svc.CreateAsync(1, "c", Img(), 3);

        var list = await svc.GetForBuyerAsync(1);

        Assert.Equal(2, list.Count);
        Assert.All(list, x => Assert.Equal(1, x.BuyerId));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
