using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using WebMail;
using WebMail.Data;
using WebMail.Domain;

namespace WebMail.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class BuyersModel : PageModel
{
    private readonly WebMailDbContext _db;
    private readonly IStringLocalizer<SharedResource> _loc;

    public BuyersModel(WebMailDbContext db, IStringLocalizer<SharedResource> loc)
    {
        _db = db;
        _loc = loc;
    }

    public IReadOnlyList<Domain.Buyer> Buyers { get; private set; } = Array.Empty<Domain.Buyer>();
    public string? Message { get; private set; }

    [BindProperty(SupportsGet = true)] public BuyerStatus? StatusFilter { get; set; }
    [BindProperty(SupportsGet = true)] public EmailAuthorizationStatus? EmailFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? CardNo { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostApproveAsync(long id) => await ReviewAsync(id, BuyerStatus.Approved);

    public async Task<IActionResult> OnPostRejectAsync(long id) => await ReviewAsync(id, BuyerStatus.Rejected);

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.Id == id && !b.IsDeleted);
        if (buyer is not null)
        {
            buyer.IsDeleted = true;
            long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId);
            _db.AuditLogs.Add(new AuditLog
            {
                Action = "AdminDeleteBuyer",
                UserId = adminId == 0 ? null : adminId,
                Details = $"buyer={id}"
            });
            await _db.SaveChangesAsync();
            Message = _loc["Admin.Buyers.Deleted"];
        }
        else
        {
            Message = _loc["Admin.Buyers.DeleteFailed"];
        }

        await LoadAsync();
        return Page();
    }

    private async Task<IActionResult> ReviewAsync(long id, BuyerStatus decision)
    {
        var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.Id == id && !b.IsDeleted);
        if (buyer is not null && buyer.BuyerStatus == BuyerStatus.PendingReview)
        {
            buyer.BuyerStatus = decision;
            long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId);
            _db.AuditLogs.Add(new AuditLog
            {
                Action = "AdminReview",
                UserId = adminId == 0 ? null : adminId,
                Details = $"buyer={id};decision={decision}"
            });
            await _db.SaveChangesAsync();
            Message = _loc["Admin.Buyers.Reviewed"];
        }
        else
        {
            Message = _loc["Admin.Buyers.ReviewFailed"];
        }

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        var query = _db.Buyers.Where(b => !b.IsDeleted);
        if (StatusFilter is not null)
        {
            query = query.Where(b => b.BuyerStatus == StatusFilter);
        }
        if (EmailFilter is not null)
        {
            query = query.Where(b => b.EmailStatus == EmailFilter);
        }
        if (!string.IsNullOrWhiteSpace(CardNo))
        {
            query = query.Where(b => b.CardNo.Contains(CardNo));
        }

        Buyers = await query.OrderByDescending(b => b.CreatedAt).ToListAsync();
    }
}
