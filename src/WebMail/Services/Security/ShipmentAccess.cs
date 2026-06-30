using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;

namespace WebMail.Services.Security;

public static class ShipmentAccess
{
    /// <summary>管理员可访问任意买家；供应商仅限分配给自己的买家。</summary>
    public static async Task<bool> CanAccessBuyerAsync(WebMailDbContext db, ClaimsPrincipal user, long buyerId)
    {
        if (user.IsInRole("Administrator")) return true;
        if (!long.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var uid)) return false;
        return await db.BuyerSupplierAssignments.AnyAsync(x => x.BuyerId == buyerId && x.SupplierId == uid);
    }
}
