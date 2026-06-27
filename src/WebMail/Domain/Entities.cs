namespace WebMail.Domain;

public sealed class AppUser
{
    public long Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Buyer
{
    public long Id { get; set; }
    public string CardNo { get; set; } = string.Empty;
    public CardStatus CardStatus { get; set; } = CardStatus.Unused;
    public long? SaleId { get; set; }
    public EmailAuthorizationStatus EmailStatus { get; set; } = EmailAuthorizationStatus.NotAuthorized;
    public BuyerStatus BuyerStatus { get; set; } = BuyerStatus.NotSubmitted;
    public SupplierProcessingStatus SupplierStatus { get; set; } = SupplierProcessingStatus.Unprocessed;
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
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

public sealed class EmailMessage
{
    public long Id { get; set; }
    public long BuyerId { get; set; }
    public long EmailAccountId { get; set; }
    public string ProviderMessageId { get; set; } = string.Empty;
    public string? ProviderThreadId { get; set; }
    public string Sender { get; set; } = string.Empty;
    public string Recipients { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTimeOffset SentAt { get; set; }
    public string? TextBody { get; set; }
    public string? HtmlBody { get; set; }
    public string? AttachmentMetadataJson { get; set; }
    public MailFolder Folder { get; set; } = MailFolder.Inbox;
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

public sealed class ActiveSyncWindow
{
    public long Id { get; set; }
    public long BuyerId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class SyncJob
{
    public long Id { get; set; }
    public long BuyerId { get; set; }
    public SyncJobStatus Status { get; set; } = SyncJobStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class AuditLog
{
    public long Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public long? UserId { get; set; }
    public string? Details { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
