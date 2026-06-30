using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;

namespace WebMail.Services;

public sealed record ShipmentImageInput(Stream Content, string ContentType, long Length);

public sealed record ShipmentResult(bool Success, string MessageKey, long? ShipmentId = null);

public sealed class ShipmentService
{
    private const long MaxBytes = 5L * 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, string> AllowedTypes = new Dictionary<string, string>
    {
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/webp"] = ".webp",
        ["image/gif"] = ".gif",
    };

    private readonly WebMailDbContext _db;
    private readonly SnowflakeIdGenerator _snowflake;
    private readonly string _storageRoot;

    public ShipmentService(WebMailDbContext db, SnowflakeIdGenerator snowflake, string storageRoot)
    {
        _db = db;
        _snowflake = snowflake;
        _storageRoot = storageRoot;
    }

    public string GetFilePath(Shipment s) => Path.Combine(_storageRoot, s.StoredFileName);

    public Task<Shipment?> GetByIdAsync(long shipmentId) =>
        _db.Shipments.FirstOrDefaultAsync(x => x.Id == shipmentId);

    public async Task<IReadOnlyList<Shipment>> GetForBuyerAsync(long buyerId) =>
        await _db.Shipments.Where(x => x.BuyerId == buyerId)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .ToListAsync();

    public async Task<ShipmentResult> CreateAsync(long buyerId, string? description, ShipmentImageInput? image, long? userId)
    {
        if (image is null || image.Length <= 0 || image.Length > MaxBytes
            || !AllowedTypes.TryGetValue(image.ContentType, out var ext))
        {
            return new ShipmentResult(false, "Shipment.InvalidImage");
        }

        Directory.CreateDirectory(_storageRoot);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff");
        var rand = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var fileName = $"{stamp}_{rand}{ext}";
        var fullPath = Path.Combine(_storageRoot, fileName);

        await using (var dest = File.Create(fullPath))
        {
            image.Content.Position = 0;
            await image.Content.CopyToAsync(dest);
        }

        var shipment = new Shipment
        {
            BuyerId = buyerId,
            ShipmentNo = _snowflake.NextId(),
            StoredFileName = fileName,
            ContentType = image.ContentType,
            Description = description?.Trim() ?? string.Empty,
            CreatedByUserId = userId,
        };
        _db.Shipments.Add(shipment);
        _db.AuditLogs.Add(new AuditLog
        {
            Action = "CreateShipment",
            UserId = userId,
            Details = $"buyer={buyerId};shipment={shipment.ShipmentNo}"
        });
        try
        {
            await _db.SaveChangesAsync();
        }
        catch
        {
            if (File.Exists(fullPath)) File.Delete(fullPath);
            throw;
        }

        return new ShipmentResult(true, "Shipment.Added", shipment.Id);
    }

    public async Task<bool> DeleteAsync(long shipmentId, long? userId)
    {
        var shipment = await _db.Shipments.FirstOrDefaultAsync(x => x.Id == shipmentId);
        if (shipment is null) return false;

        var path = GetFilePath(shipment);
        if (File.Exists(path)) File.Delete(path);

        _db.Shipments.Remove(shipment);
        _db.AuditLogs.Add(new AuditLog
        {
            Action = "DeleteShipment",
            UserId = userId,
            Details = $"buyer={shipment.BuyerId};shipment={shipment.ShipmentNo}"
        });
        await _db.SaveChangesAsync();
        return true;
    }
}
