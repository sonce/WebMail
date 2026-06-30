using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebMail.Data;
using WebMail.Services;
using WebMail.Services.Security;

namespace WebMail.Pages.Shipments;

[Authorize]
public class ImageModel : PageModel
{
    private readonly WebMailDbContext _db;
    private readonly ShipmentService _shipments;

    public ImageModel(WebMailDbContext db, ShipmentService shipments)
    {
        _db = db;
        _shipments = shipments;
    }

    public async Task<IActionResult> OnGetAsync(long id)
    {
        var shipment = await _shipments.GetByIdAsync(id);
        if (shipment is null) return NotFound();

        if (!await ShipmentAccess.CanAccessBuyerAsync(_db, User, shipment.BuyerId))
            return Forbid();

        var path = _shipments.GetFilePath(shipment);
        if (!System.IO.File.Exists(path)) return NotFound();

        return PhysicalFile(path, shipment.ContentType);
    }
}
