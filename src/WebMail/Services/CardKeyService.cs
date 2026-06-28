using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;

namespace WebMail.Services;

public sealed record CardKeyResult(bool Success, string Message, int GeneratedCount = 0);

public sealed record CardKeyListItem(
    long Id,
    string CardNo,
    CardStatus Status,
    long? SaleId,
    string? SaleDisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UsedAt);

public sealed record SaleOption(long Id, string DisplayName);

public sealed class CardKeyService
{
    public const int MaxGenerateCount = 100;

    private readonly WebMailDbContext _db;
    private readonly CardGenerationService _cardGen;

    public CardKeyService(WebMailDbContext db, CardGenerationService cardGen)
    {
        _db = db;
        _cardGen = cardGen;
    }

    public async Task<CardKeyResult> GenerateAsync(int count, long? saleId, long? actingAdminId)
    {
        if (count < 1 || count > MaxGenerateCount)
        {
            return new(false, "CardKey.CountInvalid");
        }

        if (saleId is not null
            && !await _db.Users.AnyAsync(u => u.Id == saleId && u.Role == UserRole.Sales))
        {
            return new(false, "CardKey.SaleInvalid");
        }

        var generated = new HashSet<string>();
        while (generated.Count < count)
        {
            var candidate = _cardGen.GenerateCardNo();
            if (!generated.Add(candidate))
            {
                continue;
            }
            if (await _db.Buyers.AnyAsync(b => b.CardNo == candidate))
            {
                generated.Remove(candidate);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var sent = saleId is not null;
        foreach (var cardNo in generated)
        {
            _db.Buyers.Add(new Buyer
            {
                CardNo = cardNo,
                CardStatus = CardStatus.Unused,
                SaleId = saleId,
                CardSendStatus = sent ? CardSendStatus.Sent : CardSendStatus.NotSent,
                CardSentAt = sent ? now : null
            });
        }

        _db.AuditLogs.Add(new AuditLog
        {
            Action = "AdminGenerateCardKeys",
            UserId = actingAdminId,
            Details = $"count={count};sale={saleId}"
        });
        await _db.SaveChangesAsync();
        return new(true, "CardKey.Generated", count);
    }

    public async Task<CardKeyResult> DeleteAsync(long id, long? actingAdminId)
    {
        var buyer = await _db.Buyers.FirstOrDefaultAsync(b => b.Id == id && !b.IsDeleted);
        if (buyer is null)
        {
            return new(false, "CardKey.DeleteFailed");
        }

        buyer.IsDeleted = true;
        buyer.CardStatus = CardStatus.DeletedOrDisabled;
        _db.AuditLogs.Add(new AuditLog
        {
            Action = "AdminDeleteCardKey",
            UserId = actingAdminId,
            Details = $"buyer={id}"
        });
        await _db.SaveChangesAsync();
        return new(true, "CardKey.Deleted");
    }

    public async Task<IReadOnlyList<CardKeyListItem>> ListAsync(CardStatus? status, long? saleId, string? cardNo)
    {
        var query = _db.Buyers.Where(b => !b.IsDeleted);
        if (status is not null)
        {
            query = query.Where(b => b.CardStatus == status);
        }
        if (saleId is not null)
        {
            query = query.Where(b => b.SaleId == saleId);
        }
        if (!string.IsNullOrWhiteSpace(cardNo))
        {
            query = query.Where(b => b.CardNo.Contains(cardNo));
        }

        var buyers = await query.OrderByDescending(b => b.CreatedAt).ToListAsync();
        var saleNames = await _db.Users
            .Where(u => u.Role == UserRole.Sales)
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        return buyers.Select(b => new CardKeyListItem(
            b.Id,
            b.CardNo,
            b.CardStatus,
            b.SaleId,
            b.SaleId is not null && saleNames.TryGetValue(b.SaleId.Value, out var name) ? name : null,
            b.CreatedAt,
            b.CardUsedAt)).ToList();
    }

    public async Task<IReadOnlyList<SaleOption>> ListSalesAsync()
    {
        return await _db.Users
            .Where(u => u.Role == UserRole.Sales)
            .OrderBy(u => u.DisplayName)
            .Select(u => new SaleOption(u.Id, u.DisplayName))
            .ToListAsync();
    }
}
