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
    private readonly IStringLocalizer<SharedResource> _loc;

    public MailModel(WebMailDbContext db, ShipmentService shipments, IStringLocalizer<SharedResource> loc)
    {
        _db = db;
        _shipments = shipments;
        _loc = loc;
    }

    private Task<bool> IsBuyerReadyAsync(long buyerId) => _db.Buyers.AnyAsync(b => b.Id == buyerId
        && !b.IsDeleted
        && b.ReviewStatus == ReviewStatus.Approved
        && b.EmailStatus == EmailAuthorizationStatus.Authorized);

    public long BuyerId { get; private set; }
    public IReadOnlyList<EmailMessage> Messages { get; private set; } = Array.Empty<EmailMessage>();
    public IReadOnlyList<Shipment> Shipments { get; private set; } = Array.Empty<Shipment>();
    public DateTimeOffset ActiveWindowExpiresAt { get; private set; }
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

        var account = await _db.EmailAccounts.FirstOrDefaultAsync(a => a.BuyerId == buyerId);
        if (account is not null)
        {
            Messages = await _db.EmailMessages
                .Where(m => m.EmailAccountId == account.Id)
                .OrderByDescending(m => m.SentAt)
                .ToListAsync();
        }

        Shipments = await _shipments.GetForBuyerAsync(buyerId);

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        var window = await _db.ActiveSyncWindows.FirstOrDefaultAsync(w => w.BuyerId == buyerId);
        if (window is null)
        {
            _db.ActiveSyncWindows.Add(new ActiveSyncWindow { BuyerId = buyerId, ExpiresAt = expiresAt });
        }
        else
        {
            window.ExpiresAt = expiresAt;
        }

        await _db.SaveChangesAsync();
        ActiveWindowExpiresAt = expiresAt;

        if (msg is not null && KnownMessageKeys.Contains(msg))
        {
            Message = _loc[msg];
        }

        return Page();
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
