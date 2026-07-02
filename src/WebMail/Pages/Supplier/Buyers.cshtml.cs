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

    public IReadOnlyList<BuyerRowView> Buyers { get; private set; } = Array.Empty<BuyerRowView>();
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
        // 未处理(待办)买家要求邮箱已授权（供应商需读邮件处理）；
        // 失败/完成 的买家即使后续邮箱解除了授权也仍保留在列表中。
        // 左联 EmailAccounts 取授权邮箱用于列表展示。
        Buyers = await (from a in _db.BuyerSupplierAssignments
                        where a.SupplierId == supplierId
                            && !a.Buyer.IsDeleted
                            && a.Buyer.ReviewStatus == ReviewStatus.Approved
                            && (a.Buyer.EmailStatus == EmailAuthorizationStatus.Authorized
                                || a.Buyer.SupplierStatus == SupplierProcessingStatus.Failed
                                || a.Buyer.SupplierStatus == SupplierProcessingStatus.Completed)
                        join e in _db.EmailAccounts on a.BuyerId equals e.BuyerId into eg
                        from e in eg.DefaultIfEmpty()
                        select new BuyerRowView(a.Buyer, e != null ? e.Email : null))
                        .ToListAsync();
    }
}

public sealed record BuyerRowView(Domain.Buyer Buyer, string? Email);
