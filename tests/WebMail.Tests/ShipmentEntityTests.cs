using System;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using Xunit;

namespace WebMail.Tests;

public sealed class ShipmentEntityTests
{
    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    [Fact]
    public async Task PersistsAndReloadsShipment()
    {
        await using var db = CreateDb();
        db.Shipments.Add(new Shipment
        {
            BuyerId = 7,
            ShipmentNo = 123456789012345,
            StoredFileName = "20260629T120000000_abc123.jpg",
            ContentType = "image/jpeg",
            Description = "box photo",
            CreatedByUserId = 2
        });
        await db.SaveChangesAsync();

        var loaded = await db.Shipments.SingleAsync();
        Assert.Equal(7, loaded.BuyerId);
        Assert.Equal(123456789012345, loaded.ShipmentNo);
        Assert.Equal("image/jpeg", loaded.ContentType);
        Assert.Equal("box photo", loaded.Description);
        Assert.Equal(2, loaded.CreatedByUserId);
        Assert.True(loaded.CreatedAt <= DateTimeOffset.UtcNow);
    }
}
