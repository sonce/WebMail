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

namespace WebMail.Pages.Supplier;

[Authorize(Policy = "SupplierOnly")]
public class BuyersModel : PageModel
{
    private readonly WebMailDbContext _db;
    private readonly BuyerRuleService _ruleService;
    private readonly IStringLocalizer<SharedResource> _loc;

    public BuyersModel(WebMailDbContext db, BuyerRuleService ruleService, IStringLocalizer<SharedResource> loc)
    {
        _db = db;
        _ruleService = ruleService;
        _loc = loc;
    }

    public IReadOnlyList<Domain.Buyer> Buyers { get; private set; } = Array.Empty<Domain.Buyer>();
    public string? Message { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var supplierId))
        {
            return Forbid();
        }

        await LoadBuyersAsync(supplierId);
        return Page();
    }

    public async Task<IActionResult> OnPostSetStatusAsync(long buyerId, SupplierProcessingStatus status)
    {
        if (!long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var supplierId))
        {
            return Forbid();
        }

        if (status is not (SupplierProcessingStatus.Unprocessed or SupplierProcessingStatus.Failed or SupplierProcessingStatus.Completed))
        {
            Message = _loc["Supplier.InvalidStatus"];
            await LoadBuyersAsync(supplierId);
            return Page();
        }

        var assignment = await _db.BuyerSupplierAssignments
            .Include(x => x.Buyer)
            .FirstOrDefaultAsync(x => x.BuyerId == buyerId && x.SupplierId == supplierId);

        if (assignment is not null && _ruleService.CanSupplierSetStatus(assignment.Buyer, assignment.SupplierId, supplierId))
        {
            assignment.Buyer.SupplierStatus = status;
            _db.AuditLogs.Add(new AuditLog
            {
                Action = "SupplierSetStatus",
                UserId = supplierId,
                Details = $"buyer={buyerId};status={status}"
            });
            await _db.SaveChangesAsync();
            Message = _loc["Supplier.StatusUpdated"];
        }
        else
        {
            Message = _loc["Supplier.StatusUpdateFailed"];
        }

        await LoadBuyersAsync(supplierId);
        return Page();
    }

    private async Task LoadBuyersAsync(long supplierId)
    {
        Buyers = await _db.BuyerSupplierAssignments
            .Include(x => x.Buyer)
            .Where(x => x.SupplierId == supplierId
                && !x.Buyer.IsDeleted
                && x.Buyer.ReviewStatus == ReviewStatus.Approved
                && x.Buyer.EmailStatus == EmailAuthorizationStatus.Authorized)
            .Select(x => x.Buyer)
            .ToListAsync();
    }
}
