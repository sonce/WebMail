using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;

namespace WebMail.Pages.Supplier;

[Authorize(Policy = "SupplierOnly")]
public class BuyersModel : PageModel
{
    private readonly WebMailDbContext _db;

    public BuyersModel(WebMailDbContext db)
    {
        _db = db;
    }

    public IReadOnlyList<Domain.Buyer> Buyers { get; private set; } = Array.Empty<Domain.Buyer>();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var supplierId))
        {
            return Forbid();
        }

        Buyers = await _db.BuyerSupplierAssignments
            .Include(x => x.Buyer)
            .Where(x => x.SupplierId == supplierId && !x.Buyer.IsDeleted && x.Buyer.BuyerStatus == BuyerStatus.Approved && x.Buyer.EmailStatus == EmailAuthorizationStatus.Authorized)
            .Select(x => x.Buyer)
            .ToListAsync();

        return Page();
    }
}
