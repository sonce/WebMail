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

        var result = await service.GenerateAsync(3, saleId: sale.Id, autoApprove: false, actingAdminId: 1);

        Assert.True(result.Success);
        Assert.Equal("CardKey.Generated", result.Message);
        Assert.Equal(3, result.GeneratedCount);
        var cards = await db.Buyers.ToListAsync();
        Assert.Equal(3, cards.Count);
        Assert.All(cards, c => Assert.Equal(BuyerStage.Sent, c.Stage));
        Assert.All(cards, c => Assert.Equal(sale.Id, c.SaleId));
        Assert.All(cards, c => Assert.NotNull(c.CardSentAt));
        Assert.Equal(3, cards.Select(c => c.CardNo).Distinct().Count());
    }

    [Fact]
    public async Task GenerateWithoutSaleLeavesSaleIdNull()
    {
        await using var db = CreateDb();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.GenerateAsync(1, saleId: null, autoApprove: false, actingAdminId: 1);

        Assert.True(result.Success);
        var card = await db.Buyers.SingleAsync();
        Assert.Null(card.SaleId);
        Assert.Equal(BuyerStage.NotSent, card.Stage);
        Assert.Null(card.CardSentAt);
    }

    [Fact]
    public async Task GenerateWithAutoApproveMarksBuyers()
    {
        await using var db = CreateDb();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.GenerateAsync(2, null, autoApprove: true, actingAdminId: null);

        Assert.True(result.Success);
        var cards = await db.Buyers.ToListAsync();
        Assert.Equal(2, cards.Count);
        Assert.All(cards, c => Assert.True(c.AutoApprove));
    }

    [Fact]
    public async Task GenerateWithoutAutoApproveLeavesFlagFalse()
    {
        await using var db = CreateDb();
        var service = new CardKeyService(db, new CardGenerationService());

        await service.GenerateAsync(1, null, autoApprove: false, actingAdminId: null);

        var card = await db.Buyers.SingleAsync();
        Assert.False(card.AutoApprove);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task GenerateRejectsOutOfRangeCount(int count)
    {
        await using var db = CreateDb();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.GenerateAsync(count, saleId: null, autoApprove: false, actingAdminId: 1);

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

        var result = await service.GenerateAsync(1, saleId: 9, autoApprove: false, actingAdminId: 1);

        Assert.False(result.Success);
        Assert.Equal("CardKey.SaleInvalid", result.Message);
        Assert.Empty(await db.Buyers.ToListAsync());
    }

    [Fact]
    public async Task DeleteSoftDeletesCard()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "c1", Stage = BuyerStage.NotSent });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.DeleteAsync(1, actingAdminId: 1);

        Assert.True(result.Success);
        var buyer = await db.Buyers.SingleAsync();
        Assert.True(buyer.IsDeleted);
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
    public async Task ListFiltersByStageSaleAndCardAndExcludesDeleted()
    {
        await using var db = CreateDb();
        SeedSale(db, id: 5, name: "Alice");
        db.Buyers.AddRange(
            new Buyer { Id = 1, CardNo = "alpha", Stage = BuyerStage.Sent, SaleId = 5 },
            new Buyer { Id = 2, CardNo = "beta", Stage = BuyerStage.Submitted, SaleId = 5 },
            new Buyer { Id = 3, CardNo = "gamma", Stage = BuyerStage.NotSent, SaleId = null },
            new Buyer { Id = 4, CardNo = "alpha-del", Stage = BuyerStage.Sent, SaleId = 5, IsDeleted = true });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        // Not-sent tab returns only NotSent inventory.
        var notSentTab = await service.ListAsync(null, null, null, sentTab: false);
        Assert.Equal(new[] { 3L }, notSentTab.Select(c => c.Id).ToArray());

        // Sent tab excludes NotSent and deleted.
        var sentTab = await service.ListAsync(null, null, null, sentTab: true);
        Assert.Equal(new[] { 1L, 2L }, sentTab.Select(c => c.Id).OrderBy(x => x).ToArray());

        // Stage filter on sent tab.
        var byStage = await service.ListAsync(BuyerStage.Submitted, null, null, sentTab: true);
        Assert.Equal(new[] { 2L }, byStage.Select(c => c.Id).ToArray());

        // Sale filter.
        var bySale = await service.ListAsync(null, 5, null, sentTab: true);
        Assert.Equal(new[] { 1L, 2L }, bySale.Select(c => c.Id).OrderBy(x => x).ToArray());
        Assert.All(bySale, c => Assert.Equal("Alice", c.SaleDisplayName));

        // Card-number filter, deleted excluded.
        var byCard = await service.ListAsync(null, null, "alph", sentTab: true);
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
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "c1", Stage = BuyerStage.NotSent });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.SendAsync(new[] { 1L }, saleId: 5, autoApprove: false, actingAdminId: 7);

        Assert.True(result.Success);
        Assert.Equal("CardKey.Sent", result.Message);
        Assert.Equal(1, result.GeneratedCount);
        var card = await db.Buyers.SingleAsync();
        Assert.Equal(5, card.SaleId);
        Assert.Equal(BuyerStage.Sent, card.Stage);
        Assert.NotNull(card.CardSentAt);
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == "AdminSendCardKeys"));
    }

    [Fact]
    public async Task SendWithoutSaleMarksSentAndLeavesSaleUnassigned()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "c1", Stage = BuyerStage.NotSent });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.SendAsync(new[] { 1L }, saleId: null, autoApprove: false, actingAdminId: 7);

        Assert.True(result.Success);
        Assert.Equal("CardKey.Sent", result.Message);
        var card = await db.Buyers.SingleAsync();
        Assert.Null(card.SaleId);
        Assert.Equal(BuyerStage.Sent, card.Stage);
        Assert.NotNull(card.CardSentAt);
    }

    [Fact]
    public async Task SendBatchSendsMultiple()
    {
        await using var db = CreateDb();
        SeedSale(db, id: 5, name: "Alice");
        db.Buyers.AddRange(
            new Buyer { Id = 1, CardNo = "c1", Stage = BuyerStage.NotSent },
            new Buyer { Id = 2, CardNo = "c2", Stage = BuyerStage.NotSent });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.SendAsync(new[] { 1L, 2L }, saleId: 5, autoApprove: false, actingAdminId: 7);

        Assert.True(result.Success);
        Assert.Equal(2, result.GeneratedCount);
        Assert.All(await db.Buyers.ToListAsync(), c => Assert.Equal(BuyerStage.Sent, c.Stage));
    }

    [Fact]
    public async Task SendMixedBatchSendsOnlyNotSentAndPreservesSent()
    {
        await using var db = CreateDb();
        SeedSale(db, id: 5, name: "Alice");
        db.Buyers.AddRange(
            new Buyer { Id = 1, CardNo = "c1", Stage = BuyerStage.NotSent },
            new Buyer { Id = 2, CardNo = "c2", Stage = BuyerStage.Sent, SaleId = 6 });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.SendAsync(new[] { 1L, 2L }, saleId: 5, autoApprove: false, actingAdminId: 7);

        Assert.True(result.Success);
        Assert.Equal(1, result.GeneratedCount);
        var c1 = await db.Buyers.SingleAsync(b => b.Id == 1);
        var c2 = await db.Buyers.SingleAsync(b => b.Id == 2);
        Assert.Equal(BuyerStage.Sent, c1.Stage);
        Assert.Equal(5, c1.SaleId);
        Assert.Equal(6, c2.SaleId); // already-sent card untouched
    }

    [Fact]
    public async Task SendSkipsAlreadySentCards()
    {
        await using var db = CreateDb();
        SeedSale(db, id: 5, name: "Alice");
        SeedSale(db, id: 6, name: "Bob");
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "c1", Stage = BuyerStage.Sent, SaleId = 6 });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.SendAsync(new[] { 1L }, saleId: 5, autoApprove: false, actingAdminId: 7);

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
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "c1", Stage = BuyerStage.NotSent });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.SendAsync(new[] { 1L }, saleId: 9, autoApprove: false, actingAdminId: 7);

        Assert.False(result.Success);
        Assert.Equal("CardKey.SaleInvalid", result.Message);
        Assert.Equal(BuyerStage.NotSent, (await db.Buyers.SingleAsync()).Stage);
    }

    [Fact]
    public async Task SendWithNoEligibleCardsReturnsNoneSelected()
    {
        await using var db = CreateDb();
        SeedSale(db, id: 5, name: "Alice");
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var result = await service.SendAsync(Array.Empty<long>(), saleId: 5, autoApprove: false, actingAdminId: 7);

        Assert.False(result.Success);
        Assert.Equal("CardKey.SendNoneSelected", result.Message);
    }

    [Fact]
    public async Task SendWithAutoApproveOverridesEveryTarget()
    {
        await using var db = CreateDb();
        var service = new CardKeyService(db, new CardGenerationService());
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "a", Stage = BuyerStage.NotSent, AutoApprove = true });
        db.Buyers.Add(new Buyer { Id = 2, CardNo = "b", Stage = BuyerStage.NotSent, AutoApprove = false });
        await db.SaveChangesAsync();

        var result = await service.SendAsync(new[] { 1L, 2L }, saleId: null, autoApprove: true, actingAdminId: null);

        Assert.True(result.Success);
        Assert.All(await db.Buyers.ToListAsync(), b => Assert.True(b.AutoApprove));
    }

    [Fact]
    public async Task SendWithoutAutoApproveClearsPreviouslyAutoApproved()
    {
        await using var db = CreateDb();
        var service = new CardKeyService(db, new CardGenerationService());
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "a", Stage = BuyerStage.NotSent, AutoApprove = true });
        await db.SaveChangesAsync();

        await service.SendAsync(new[] { 1L }, saleId: null, autoApprove: false, actingAdminId: null);

        var buyer = await db.Buyers.SingleAsync();
        Assert.False(buyer.AutoApprove);
    }

    [Fact]
    public async Task ListFiltersBySentTab()
    {
        await using var db = CreateDb();
        db.Buyers.AddRange(
            new Buyer { Id = 1, CardNo = "c1", Stage = BuyerStage.NotSent },
            new Buyer { Id = 2, CardNo = "c2", Stage = BuyerStage.Sent, SaleId = null });
        await db.SaveChangesAsync();
        var service = new CardKeyService(db, new CardGenerationService());

        var notSent = await service.ListAsync(null, null, null, sentTab: false);
        Assert.Equal(new[] { 1L }, notSent.Select(c => c.Id).ToArray());

        var sent = await service.ListAsync(null, null, null, sentTab: true);
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
