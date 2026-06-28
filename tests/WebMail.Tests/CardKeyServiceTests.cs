using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class CardKeyServiceTests
{
    [Fact]
    public async Task GenerateCreatesUnusedCardsBoundToSale()
    {
        await using var db = CreateDb();
        var sale = SeedSale(db, id: 5, name: "Alice");
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.GenerateAsync(3, saleId: sale.Id, actingAdminId: 1);

        Assert.True(result.Success);
        Assert.Equal("CardKey.Generated", result.Message);
        Assert.Equal(3, result.GeneratedCount);
        var cards = await db.Buyers.ToListAsync();
        Assert.Equal(3, cards.Count);
        Assert.All(cards, c => Assert.Equal(CardStatus.Unused, c.CardStatus));
        Assert.All(cards, c => Assert.Equal(sale.Id, c.SaleId));
        Assert.All(cards, c => Assert.Equal(CardSendStatus.Sent, c.CardSendStatus));
        Assert.All(cards, c => Assert.NotNull(c.CardSentAt));
        Assert.Equal(3, cards.Select(c => c.CardNo).Distinct().Count());
    }

    [Fact]
    public async Task GenerateWithoutSaleLeavesSaleIdNull()
    {
        await using var db = CreateDb();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.GenerateAsync(1, saleId: null, actingAdminId: 1);

        Assert.True(result.Success);
        var card = await db.Buyers.SingleAsync();
        Assert.Null(card.SaleId);
        Assert.Equal(CardSendStatus.NotSent, card.CardSendStatus);
        Assert.Null(card.CardSentAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task GenerateRejectsOutOfRangeCount(int count)
    {
        await using var db = CreateDb();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.GenerateAsync(count, saleId: null, actingAdminId: 1);

        Assert.False(result.Success);
        Assert.Equal("CardKey.CountInvalid", result.Message);
        Assert.Empty(await db.Buyers.ToListAsync());
    }

    [Fact]
    public async Task GenerateRejectsNonSaleSaleId()
    {
        await using var db = CreateDb();
        db.Users.Add(new AppUser { Id = 9, UserName = "sup", DisplayName = "Sup", Role = UserRole.Supplier });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.GenerateAsync(1, saleId: 9, actingAdminId: 1);

        Assert.False(result.Success);
        Assert.Equal("CardKey.SaleInvalid", result.Message);
        Assert.Empty(await db.Buyers.ToListAsync());
    }

    [Fact]
    public async Task DeleteSoftDeletesAndMarksDisabled()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "c1", CardStatus = CardStatus.Unused });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.DeleteAsync(1, actingAdminId: 1);

        Assert.True(result.Success);
        var buyer = await db.Buyers.SingleAsync();
        Assert.True(buyer.IsDeleted);
        Assert.Equal(CardStatus.DeletedOrDisabled, buyer.CardStatus);
    }

    [Fact]
    public async Task DeleteMissingReturnsFailure()
    {
        await using var db = CreateDb();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.DeleteAsync(404, actingAdminId: 1);

        Assert.False(result.Success);
        Assert.Equal("CardKey.DeleteFailed", result.Message);
    }

    [Fact]
    public async Task ListFiltersByStatusSaleAndCardAndExcludesDeleted()
    {
        await using var db = CreateDb();
        SeedSale(db, id: 5, name: "Alice");
        db.Buyers.AddRange(
            new Buyer { Id = 1, CardNo = "alpha", CardStatus = CardStatus.Unused, SaleId = 5 },
            new Buyer { Id = 2, CardNo = "beta", CardStatus = CardStatus.Authorized, SaleId = 5 },
            new Buyer { Id = 3, CardNo = "gamma", CardStatus = CardStatus.Unused, SaleId = null },
            new Buyer { Id = 4, CardNo = "alpha-del", CardStatus = CardStatus.Unused, SaleId = 5, IsDeleted = true });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var byStatus = await service.ListAsync(CardStatus.Unused, null, null);
        Assert.Equal(new[] { 1L, 3L }, byStatus.Select(c => c.Id).OrderBy(x => x).ToArray());

        var bySale = await service.ListAsync(null, 5, null);
        Assert.Equal(new[] { 1L, 2L }, bySale.Select(c => c.Id).OrderBy(x => x).ToArray());
        Assert.All(bySale, c => Assert.Equal("Alice", c.SaleDisplayName));

        var byCard = await service.ListAsync(null, null, "alph");
        Assert.Equal(new[] { 1L }, byCard.Select(c => c.Id).ToArray());
    }

    [Fact]
    public async Task ListSalesReturnsOnlySaleUsers()
    {
        await using var db = CreateDb();
        SeedSale(db, id: 5, name: "Alice");
        db.Users.Add(new AppUser { Id = 9, UserName = "sup", DisplayName = "Sup", Role = UserRole.Supplier });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var sales = await service.ListSalesAsync();

        Assert.Single(sales);
        Assert.Equal(5, sales[0].Id);
        Assert.Equal("Alice", sales[0].DisplayName);
    }

    [Fact]
    public async Task SendAssignsSaleMarksSentAndStampsTime()
    {
        await using var db = CreateDb();
        SeedSale(db, id: 5, name: "Alice");
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "c1", CardSendStatus = CardSendStatus.NotSent });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.SendAsync(new[] { 1L }, saleId: 5, actingAdminId: 7);

        Assert.True(result.Success);
        Assert.Equal("CardKey.Sent", result.Message);
        Assert.Equal(1, result.GeneratedCount);
        var card = await db.Buyers.SingleAsync();
        Assert.Equal(5, card.SaleId);
        Assert.Equal(CardSendStatus.Sent, card.CardSendStatus);
        Assert.NotNull(card.CardSentAt);
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == "AdminSendCardKeys"));
    }

    [Fact]
    public async Task SendBatchSendsMultiple()
    {
        await using var db = CreateDb();
        SeedSale(db, id: 5, name: "Alice");
        db.Buyers.AddRange(
            new Buyer { Id = 1, CardNo = "c1", CardSendStatus = CardSendStatus.NotSent },
            new Buyer { Id = 2, CardNo = "c2", CardSendStatus = CardSendStatus.NotSent });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.SendAsync(new[] { 1L, 2L }, saleId: 5, actingAdminId: 7);

        Assert.True(result.Success);
        Assert.Equal(2, result.GeneratedCount);
        Assert.All(await db.Buyers.ToListAsync(), c => Assert.Equal(CardSendStatus.Sent, c.CardSendStatus));
    }

    [Fact]
    public async Task SendMixedBatchSendsOnlyNotSentAndPreservesSent()
    {
        await using var db = CreateDb();
        SeedSale(db, id: 5, name: "Alice");
        db.Buyers.AddRange(
            new Buyer { Id = 1, CardNo = "c1", CardSendStatus = CardSendStatus.NotSent },
            new Buyer { Id = 2, CardNo = "c2", CardSendStatus = CardSendStatus.Sent, SaleId = 6 });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.SendAsync(new[] { 1L, 2L }, saleId: 5, actingAdminId: 7);

        Assert.True(result.Success);
        Assert.Equal(1, result.GeneratedCount);
        var c1 = await db.Buyers.SingleAsync(b => b.Id == 1);
        var c2 = await db.Buyers.SingleAsync(b => b.Id == 2);
        Assert.Equal(CardSendStatus.Sent, c1.CardSendStatus);
        Assert.Equal(5, c1.SaleId);
        Assert.Equal(6, c2.SaleId); // already-sent card untouched
    }

    [Fact]
    public async Task SendSkipsAlreadySentCards()
    {
        await using var db = CreateDb();
        SeedSale(db, id: 5, name: "Alice");
        SeedSale(db, id: 6, name: "Bob");
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "c1", CardSendStatus = CardSendStatus.Sent, SaleId = 6 });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.SendAsync(new[] { 1L }, saleId: 5, actingAdminId: 7);

        Assert.False(result.Success);
        Assert.Equal("CardKey.SendNoneSelected", result.Message);
        var card = await db.Buyers.SingleAsync();
        Assert.Equal(6, card.SaleId); // 原销售未被覆盖
    }

    [Fact]
    public async Task SendRejectsNonSaleSaleId()
    {
        await using var db = CreateDb();
        db.Users.Add(new AppUser { Id = 9, UserName = "sup", DisplayName = "Sup", Role = UserRole.Supplier });
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "c1", CardSendStatus = CardSendStatus.NotSent });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.SendAsync(new[] { 1L }, saleId: 9, actingAdminId: 7);

        Assert.False(result.Success);
        Assert.Equal("CardKey.SaleInvalid", result.Message);
        Assert.Equal(CardSendStatus.NotSent, (await db.Buyers.SingleAsync()).CardSendStatus);
    }

    [Fact]
    public async Task SendWithNoEligibleCardsReturnsNoneSelected()
    {
        await using var db = CreateDb();
        SeedSale(db, id: 5, name: "Alice");
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.SendAsync(Array.Empty<long>(), saleId: 5, actingAdminId: 7);

        Assert.False(result.Success);
        Assert.Equal("CardKey.SendNoneSelected", result.Message);
    }

    [Fact]
    public async Task ListFiltersBySendStatus()
    {
        await using var db = CreateDb();
        db.Buyers.AddRange(
            new Buyer { Id = 1, CardNo = "c1", CardSendStatus = CardSendStatus.NotSent },
            new Buyer { Id = 2, CardNo = "c2", CardSendStatus = CardSendStatus.Sent, SaleId = null });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var notSent = await service.ListAsync(null, null, null, CardSendStatus.NotSent);
        Assert.Equal(new[] { 1L }, notSent.Select(c => c.Id).ToArray());

        var sent = await service.ListAsync(null, null, null, CardSendStatus.Sent);
        Assert.Equal(new[] { 2L }, sent.Select(c => c.Id).ToArray());
    }

    private static AppUser SeedSale(WebMailDbContext db, long id, string name)
    {
        var user = new AppUser { Id = id, UserName = $"u{id}", DisplayName = name, Role = UserRole.Sales };
        db.Users.Add(user);
        return user;
    }

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
