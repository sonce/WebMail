using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;

namespace WebMail.Pages.Supplier;

[Authorize(Policy = "SupplierOnly")]
public class MailModel : PageModel
{
    private readonly WebMailDbContext _db;

    public MailModel(WebMailDbContext db)
    {
        _db = db;
    }

    public long BuyerId { get; private set; }
    public IReadOnlyList<EmailMessage> Messages { get; private set; } = Array.Empty<EmailMessage>();
    public DateTimeOffset ActiveWindowExpiresAt { get; private set; }

    public async Task<IActionResult> OnGetAsync(long buyerId)
    {
        if (!long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var supplierId))
        {
            return Forbid();
        }

        var authorized = await _db.BuyerSupplierAssignments
            .AnyAsync(x => x.BuyerId == buyerId
                && x.SupplierId == supplierId
                && !x.Buyer.IsDeleted
                && x.Buyer.EmailStatus == EmailAuthorizationStatus.Normal);

        if (!authorized)
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

        return Page();
    }
}
