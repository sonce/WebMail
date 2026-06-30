namespace WebMail.Domain;

public sealed class AppUser
{
    public long Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsActive { get; set; } = true;
}

public sealed class Buyer
{
    public long Id { get; set; }
    public string CardNo { get; set; } = string.Empty;
    public BuyerStage Stage { get; set; } = BuyerStage.NotSent;
    public bool AutoApprove { get; set; }
    public DateTimeOffset? CardSentAt { get; set; }
    public long? SaleId { get; set; }
    public EmailAuthorizationStatus EmailStatus { get; set; } = EmailAuthorizationStatus.NotAuthorized;
    public ReviewStatus ReviewStatus { get; set; } = ReviewStatus.Pending;
    public SupplierProcessingStatus SupplierStatus { get; set; } = SupplierProcessingStatus.Unprocessed;
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CardUsedAt { get; set; }
}

public sealed class EmailAccount
{
    public long Id { get; set; }
    public long BuyerId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ProviderUserId { get; set; } = string.Empty;
    public string EncryptedRefreshToken { get; set; } = string.Empty;
    public string? EncryptedAccessToken { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AllowedSender
{
    public long Id { get; set; }
    public string EmailAddress { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BuyerSupplierAssignment
{
    public long Id { get; set; }
    public long BuyerId { get; set; }
    public long SupplierId { get; set; }
    public Buyer Buyer { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AuditLog
{
    public long Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public long? UserId { get; set; }
    public string? Details { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Shipment
{
    public long Id { get; set; }
    public long BuyerId { get; set; }
    public long ShipmentNo { get; set; }
    public string StoredFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public long? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
