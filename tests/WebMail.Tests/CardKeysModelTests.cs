using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Pages.Admin;
using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class CardKeysModelTests
{
    [Fact]
    public async Task GenerateHandlerCreatesCardsAndLoadsList()
    {
        await using var db = CreateDb();
        var model = CreateModel(db);
        model.GenerateCount = 3;
        model.GenerateSaleId = null;

        await model.OnPostGenerateAsync();

        Assert.Equal("CardKey.Generated", model.Message);
        Assert.Equal(3, model.Cards.Count);
        Assert.Equal(3, await db.Buyers.CountAsync());
    }

    [Fact]
    public async Task GenerateHandlerSurfacesValidationMessage()
    {
        await using var db = CreateDb();
        var model = CreateModel(db);
        model.GenerateCount = 0;

        await model.OnPostGenerateAsync();

        Assert.Equal("CardKey.CountInvalid", model.Message);
        Assert.Empty(model.Cards);
    }

    [Fact]
    public async Task DeleteHandlerSoftDeletesAndRemovesFromList()
    {
        await using var db = CreateDb();
        db.Buyers.Add(new Buyer { Id = 1, CardNo = "c1", CardStatus = CardStatus.Unused });
        await db.SaveChangesAsync();
        var model = CreateModel(db);

        await model.OnPostDeleteAsync(1);

        Assert.Equal("CardKey.Deleted", model.Message);
        Assert.Empty(model.Cards);
        Assert.True((await db.Buyers.SingleAsync()).IsDeleted);
    }

    private static CardKeysModel CreateModel(WebMailDbContext db) =>
        new(new CardKeyService(db, new CardGenerationService()), TestLocalizer.Shared)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() }
        };

    private static WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}
