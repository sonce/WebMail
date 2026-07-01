using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using WebMail;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;
using WebMail.Services.Security;

namespace WebMail.Pages.Supplier;

[Authorize(Policy = "SupplierOrAdmin")]
public class MailModel : PageModel
{
    private static readonly HashSet<string> KnownMessageKeys = new()
    {
        "Shipment.Added", "Shipment.Deleted", "Shipment.InvalidImage"
    };

    private readonly WebMailDbContext _db;
    private readonly ShipmentService _shipments;
    private readonly IMailCacheService _cache;
    private readonly IStringLocalizer<SharedResource> _loc;

    public MailModel(WebMailDbContext db, ShipmentService shipments, IMailCacheService cache, IStringLocalizer<SharedResource> loc)
    {
        _db = db;
        _shipments = shipments;
        _cache = cache;
        _loc = loc;
    }

    private Task<bool> IsBuyerReadyAsync(long buyerId) => _db.Buyers.AnyAsync(b => b.Id == buyerId
        && !b.IsDeleted
        && b.ReviewStatus == ReviewStatus.Approved
        && b.EmailStatus == EmailAuthorizationStatus.Authorized);

    public long BuyerId { get; private set; }
    public IReadOnlyList<Domain.MailMessageView> Messages { get; private set; } = Array.Empty<Domain.MailMessageView>();
    public IReadOnlyList<Shipment> Shipments { get; private set; } = Array.Empty<Shipment>();
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync(long buyerId, string? msg = null)
    {
        if (!await ShipmentAccess.CanAccessBuyerAsync(_db, User, buyerId))
        {
            return Forbid();
        }

        // 供应商额外要求买家审核通过且邮箱已授权（与原行为一致）；管理员不受限。
        if (!User.IsInRole("Administrator") && !await IsBuyerReadyAsync(buyerId))
        {
            return Forbid();
        }

        BuyerId = buyerId;

        // Mail is fetched asynchronously by the page's AJAX polling (OnGetPoll) after
        // render, so the page loads instantly instead of blocking on a Gmail fetch.
        Shipments = await _shipments.GetForBuyerAsync(buyerId);

        if (msg is not null && KnownMessageKeys.Contains(msg))
        {
            Message = _loc[msg];
        }

        return Page();
    }

    public async Task<IActionResult> OnGetPoll(long buyerId, bool force = false)
    {
        if (!await ShipmentAccess.CanAccessBuyerAsync(_db, User, buyerId))
        {
            return Forbid();
        }
        if (!User.IsInRole("Administrator") && !await IsBuyerReadyAsync(buyerId))
        {
            return Forbid();
        }

        var cached = await _cache.GetOrFetchAsync(buyerId, force, HttpContext.RequestAborted);
        return new JsonResult(new MailPollResponse(cached.Messages, cached.Stale, cached.Error));
    }

    public async Task<IActionResult> OnPostAddShipmentAsync(long buyerId, string? description, IFormFile? image)
    {
        if (!await ShipmentAccess.CanAccessBuyerAsync(_db, User, buyerId))
        {
            return Forbid();
        }

        if (!User.IsInRole("Administrator") && !await IsBuyerReadyAsync(buyerId))
        {
            return Forbid();
        }

        long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid);
        ShipmentImageInput? input = image is { Length: > 0 }
            ? new ShipmentImageInput(image.OpenReadStream(), image.ContentType, image.Length)
            : null;

        var result = await _shipments.CreateAsync(buyerId, description, input, uid == 0 ? null : uid);
        return RedirectToPage(new { buyerId, msg = result.MessageKey });
    }

    public async Task<IActionResult> OnPostDeleteShipmentAsync(long shipmentId, long buyerId)
    {
        if (!await ShipmentAccess.CanAccessBuyerAsync(_db, User, buyerId))
        {
            return Forbid();
        }

        if (!User.IsInRole("Administrator") && !await IsBuyerReadyAsync(buyerId))
        {
            return Forbid();
        }

        var shipment = await _shipments.GetByIdAsync(shipmentId);
        if (shipment is not null && shipment.BuyerId == buyerId)
        {
            long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid);
            await _shipments.DeleteAsync(shipmentId, uid == 0 ? null : uid);
            return RedirectToPage(new { buyerId, msg = "Shipment.Deleted" });
        }

        return RedirectToPage(new { buyerId });
    }
}

public sealed record MailPollResponse(IReadOnlyList<MailMessageView> Messages, bool Stale, string? Error);
