using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using Xunit;

namespace WebMail.Tests;

// Regression: SQLite cannot ORDER BY a DateTimeOffset column unless a value
// converter maps it to a sortable type. These tests run against a real SQLite
// in-memory database (the EF InMemory provider does not catch this).
public sealed class SqliteDateTimeOffsetOrderingTests
{
    [Fact]
    public async Task OrdersBuyersByCreatedAtOnSqlite()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();

        var basis = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        db.Buyers.Add(new Buyer { CardNo = "old", CreatedAt = basis });
        db.Buyers.Add(new Buyer { CardNo = "new", CreatedAt = basis.AddDays(1) });
        await db.SaveChangesAsync();

        var ordered = await db.Buyers.OrderByDescending(b => b.CreatedAt).ToListAsync();

        Assert.Equal(new[] { "new", "old" }, ordered.Select(b => b.CardNo).ToArray());
    }

    [Fact]
    public async Task DateTimeOffsetRoundTripsThroughSqlite()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();

        var when = new DateTimeOffset(2026, 6, 27, 8, 30, 0, TimeSpan.Zero);
        db.Buyers.Add(new Buyer { CardNo = "c1", CreatedAt = when });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var loaded = await db.Buyers.SingleAsync();
        Assert.Equal(when, loaded.CreatedAt);
    }

    private static WebMailDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<WebMailDbContext>().UseSqlite(connection).Options);
}
