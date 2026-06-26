using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;
using WebMail.Services;

namespace WebMail.Pages.Sales;

[Authorize(Policy = "SalesOnly")]
public class BuyersModel : PageModel
{
    private readonly WebMailDbContext _db;
    private readonly BuyerRuleService _ruleService;

    public BuyersModel(WebMailDbContext db, BuyerRuleService ruleService)
    {
        _db = db;
        _ruleService = ruleService;
    }

    public IReadOnlyList<Domain.Buyer> Buyers { get; private set; } = Array.Empty<Domain.Buyer>();
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!TryGetCurrentUserId(out var currentUserId))
        {
            return Forbid();
        }

        await LoadBuyersAsync(currentUserId);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        if (!TryGetCurrentUserId(out var currentUserId))
        {
            return Forbid();
        }

        var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.Id == id);
        if (buyer is null)
        {
            Message = "买家不存在。";
        }
        else if (_ruleService.CanSalesDeleteBuyer(buyer, currentUserId))
        {
            buyer.IsDeleted = true;
            await _db.SaveChangesAsync();
            Message = "已删除买家。";
        }
        else
        {
            Message = "无法删除该买家。";
        }

        await LoadBuyersAsync(currentUserId);
        return Page();
    }

    private async Task LoadBuyersAsync(long currentUserId)
    {
        Buyers = await _db.Buyers
            .Where(b => !b.IsDeleted && b.SaleId == currentUserId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();
    }

    private bool TryGetCurrentUserId(out long userId) =>
        long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
