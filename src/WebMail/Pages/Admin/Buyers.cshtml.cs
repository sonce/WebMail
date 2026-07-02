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

namespace WebMail.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class BuyersModel : PageModel
{
    private readonly WebMailDbContext _db;
    private readonly BuyerReviewService _reviewService;
    private readonly IStringLocalizer<SharedResource> _loc;

    public BuyersModel(WebMailDbContext db, BuyerReviewService reviewService, IStringLocalizer<SharedResource> loc)
    {
        _db = db;
        _reviewService = reviewService;
        _loc = loc;
    }

    public IReadOnlyList<Domain.Buyer> Buyers { get; private set; } = Array.Empty<Domain.Buyer>();
    public IReadOnlyList<SupplierOption> Suppliers { get; private set; } = Array.Empty<SupplierOption>();
    public IReadOnlyDictionary<long, SupplierAssignmentView> AssignmentByBuyer { get; private set; } = new Dictionary<long, SupplierAssignmentView>();
    public string? Message { get; private set; }

    [BindProperty(SupportsGet = true)] public BuyerStage? StageFilter { get; set; }
    [BindProperty(SupportsGet = true)] public ReviewStatus? ReviewFilter { get; set; }
    [BindProperty(SupportsGet = true)] public EmailAuthorizationStatus? EmailFilter { get; set; }
    [BindProperty(SupportsGet = true)] public string? CardNo { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostApproveAsync(long id) => await ReviewAsync(id, ReviewStatus.Approved);

    public async Task<IActionResult> OnPostRejectAsync(long id) => await ReviewAsync(id, ReviewStatus.Rejected);

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

    public async Task<IActionResult> OnPostAssignSupplierAsync(long buyerId, long? supplierId)
    {
        long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId);

        var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.Id == buyerId && !b.IsDeleted);
        if (buyer is null)
        {
            Message = _loc["Admin.Buyers.AssignFailed"];
            await LoadAsync();
            return Page();
        }

        if (supplierId is not null)
        {
            var supplier = await _db.Users.FirstOrDefaultAsync(u => u.Id == supplierId && u.Role == UserRole.Supplier && u.IsActive);
            if (supplier is null)
            {
                Message = _loc["Admin.Buyers.AssignFailed"];
                await LoadAsync();
                return Page();
            }
        }

        var existing = await _db.BuyerSupplierAssignments.FirstOrDefaultAsync(x => x.BuyerId == buyerId);
        var changed = false;
        if (supplierId is not null)
        {
            if (existing is null)
            {
                _db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment { BuyerId = buyerId, SupplierId = supplierId.Value });
            }
            else
            {
                existing.SupplierId = supplierId.Value;
            }
            changed = true;
        }
        else if (existing is not null)
        {
            _db.BuyerSupplierAssignments.Remove(existing);
            changed = true;
        }

        if (changed)
        {
            _db.AuditLogs.Add(new AuditLog
            {
                Action = "AdminAssignSupplier",
                UserId = adminId == 0 ? null : adminId,
                Details = $"buyer={buyerId};supplier={supplierId}"
            });
            await _db.SaveChangesAsync();
            Message = _loc["Admin.Buyers.Assigned"];
        }

        await LoadAsync();
        return Page();
    }

    private async Task<IActionResult> ReviewAsync(long id, ReviewStatus decision)
    {
        var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.Id == id && !b.IsDeleted);
        if (buyer is not null && buyer.Stage == BuyerStage.Submitted && buyer.ReviewStatus == ReviewStatus.Pending)
        {
            long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId);
            await _reviewService.ApplyReviewAsync(buyer, decision,
                adminId: adminId == 0 ? null : adminId, writeAuditLog: true, CancellationToken.None);
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
        // Un-sent cards are still in inventory (visible only on the card-key page);
        // they only become "buyers" once distributed to a sales person.
        var query = _db.Buyers.Where(b => !b.IsDeleted && b.Stage != BuyerStage.NotSent);
        if (StageFilter is not null)
        {
            query = query.Where(b => b.Stage == StageFilter);
        }
        if (ReviewFilter is not null)
        {
            query = query.Where(b => b.ReviewStatus == ReviewFilter);
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

        Suppliers = await _db.Users
            .Where(u => u.Role == UserRole.Supplier && u.IsActive)
            .OrderBy(u => u.DisplayName)
            .Select(u => new SupplierOption(u.Id, u.DisplayName))
            .ToListAsync();

        var buyerIds = Buyers.Select(b => b.Id).ToList();
        var assigned = await (from a in _db.BuyerSupplierAssignments
                              join u in _db.Users on a.SupplierId equals u.Id into gj
                              from u in gj.DefaultIfEmpty()
                              where buyerIds.Contains(a.BuyerId)
                              select new { a.BuyerId, a.SupplierId, DisplayName = u != null ? u.DisplayName : string.Empty })
                              .ToDictionaryAsync(x => x.BuyerId, x => new SupplierAssignmentView(x.SupplierId, x.DisplayName));

        var map = new Dictionary<long, SupplierAssignmentView>();
        foreach (var b in Buyers)
        {
            map[b.Id] = assigned.TryGetValue(b.Id, out var view) ? view : new SupplierAssignmentView(null, string.Empty);
        }
        AssignmentByBuyer = map;
    }
}

public sealed record SupplierOption(long Id, string DisplayName);
public sealed record SupplierAssignmentView(long? SupplierId, string DisplayName);
