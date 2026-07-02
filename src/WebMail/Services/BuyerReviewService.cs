using Microsoft.EntityFrameworkCore;
using WebMail.Data;
using WebMail.Domain;

namespace WebMail.Services;

/// <summary>
/// Shared review (审核) logic for buyers, used by both the OAuth auto-approve
/// (免审核) path and the manual admin review path. Applying an Approved
/// decision also auto-assigns the buyer to the single active supplier when
/// exactly one exists and no assignment is present yet.
/// </summary>
public sealed class BuyerReviewService(WebMailDbContext db)
{
    /// <summary>
    /// Sets the buyer's review decision, optionally writes an audit log, and
    /// when the decision is Approved auto-assigns a sole active supplier if
    /// possible. Does NOT call SaveChangesAsync — the caller owns the unit of
    /// work so it can batch related changes (e.g. the OAuth callback).
    /// </summary>
    public async Task ApplyReviewAsync(
        Buyer buyer,
        ReviewStatus decision,
        long? adminId,
        bool writeAuditLog,
        CancellationToken cancellationToken)
    {
        buyer.ReviewStatus = decision;

        if (writeAuditLog)
        {
            db.AuditLogs.Add(new AuditLog
            {
                Action = "AdminReview",
                UserId = adminId,
                Details = $"buyer={buyer.Id};decision={decision}"
            });
        }

        if (decision == ReviewStatus.Approved)
        {
            await AutoAssignSingleSupplierAsync(buyer.Id, cancellationToken);
        }
    }

    /// <summary>
    /// When exactly one active supplier exists and the buyer has no existing
    /// assignment, assigns the buyer to that supplier. Silently does nothing
    /// otherwise (no suppliers, multiple suppliers, or already assigned).
    /// No audit log — auto-assignment is a system default, not an admin action.
    /// </summary>
    private async Task AutoAssignSingleSupplierAsync(long buyerId, CancellationToken cancellationToken)
    {
        var existing = await db.BuyerSupplierAssignments
            .FirstOrDefaultAsync(x => x.BuyerId == buyerId, cancellationToken);
        if (existing is not null)
        {
            return;
        }

        var suppliers = await db.Users
            .Where(u => u.Role == UserRole.Supplier && u.IsActive)
            .ToListAsync(cancellationToken);

        if (suppliers.Count == 1)
        {
            db.BuyerSupplierAssignments.Add(new BuyerSupplierAssignment
            {
                BuyerId = buyerId,
                SupplierId = suppliers[0].Id
            });
        }
    }
}
