using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;

namespace WebMail.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class BuyersModel : PageModel
{
    private readonly WebMailDbContext _db;

    public BuyersModel(WebMailDbContext db)
    {
        _db = db;
    }

    public IReadOnlyList<Domain.Buyer> Buyers { get; private set; } = Array.Empty<Domain.Buyer>();
    public string? Message { get; private set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostApproveAsync(long id) => await ReviewAsync(id, BuyerStatus.Approved);

    public async Task<IActionResult> OnPostRejectAsync(long id) => await ReviewAsync(id, BuyerStatus.Rejected);

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
            Message = "已更新审核状态。";
        }
        else
        {
            Message = "无法审核该买家。";
        }

        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        Buyers = await _db.Buyers
            .Where(b => !b.IsDeleted)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();
    }
}
