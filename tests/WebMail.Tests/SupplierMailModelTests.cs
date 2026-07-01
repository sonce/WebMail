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
using MailPollResponse = WebMail.Pages.Supplier.MailPollResponse;
using Xunit;

namespace WebMail.Tests;

public sealed class SupplierMailModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shipmail_" + Guid.NewGuid().ToString("N"));

    private WebMailDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<WebMailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private ShipmentService Svc(WebMailDbContext db) => new(db, new SnowflakeIdGenerator(), _root);

    private MailModel CreateModel(WebMailDbContext db, long userId, string role, IMailCacheService? cache = null)
    {
        var model = new MailModel(db, Svc(db), cache ?? new StubCache(), TestLocalizer.Shared);
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, role) }, "test"));
        model.PageContext = new PageContext { HttpContext = new DefaultHttpContext { User = user } };
        return model;
    }

    private sealed class StubCache : IMailCacheService
    {
        private readonly MailCacheResult _result;
        public StubCache() => _result = new MailCacheResult(Array.Empty<MailMessageView>(), Stale: false, Error: null);
        public StubCache(IReadOnlyList<MailMessageView> messages) =>
            _result = new MailCacheResult(messages, Stale: false, Error: null);
        public Task<MailCacheResult> GetOrFetchAsync(long buyerId, bool force, CancellationToken ct) => Task.FromResult(_result);
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

    [Fact]
    public async Task OnGetDoesNotInvokeCacheSoPageRendersWithoutBlockingFetch()
    {
        // OnGet must render instantly; the Gmail fetch happens asynchronously via
        // the OnGetPoll AJAX endpoint, not during the page request.
        await using var db = CreateDb();
        db.Buyers.Add(SeedApprovedBuyer(db, 52));
        await db.SaveChangesAsync();
        var cache = new SpyCache();
        var model = CreateModel(db, userId: 1, role: "Administrator", cache);

        var result = await model.OnGetAsync(buyerId: 52);

        Assert.IsType<PageResult>(result);
        Assert.Equal(0, cache.CallCount);
        Assert.Empty(model.Messages);
    }

    [Fact]
    public async Task OnGetPollReturnsJsonMessagesForAdmin()
    {
        await using var db = CreateDb();
        db.Buyers.Add(SeedApprovedBuyer(db, 50));
        await db.SaveChangesAsync();
        var cache = new StubCache(new[]
        {
            new MailMessageView("m-1", "s@x.com", "Hello",
                new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero), MailFolder.Inbox)
        });
        var model = CreateModel(db, userId: 1, role: "Administrator", cache);

        var result = await model.OnGetPoll(buyerId: 50, force: false);

        var json = Assert.IsType<JsonResult>(result);
        var payload = Assert.IsType<MailPollResponse>(json.Value);
        Assert.Single(payload.Messages);
        Assert.False(payload.Stale);
    }

    [Fact]
    public async Task OnGetPollForbidsUnassignedSupplier()
    {
        await using var db = CreateDb();
        db.Buyers.Add(SeedApprovedBuyer(db, 51));
        await db.SaveChangesAsync();
        var model = CreateModel(db, userId: 9, role: "Supplier"); // not assigned

        var result = await model.OnGetPoll(buyerId: 51, force: false);

        Assert.IsType<ForbidResult>(result);
    }

    private sealed class SpyCache : IMailCacheService
    {
        public int CallCount { get; private set; }
        public Task<MailCacheResult> GetOrFetchAsync(long buyerId, bool force, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new MailCacheResult(Array.Empty<MailMessageView>(), Stale: false, Error: null));
        }
    }
}
