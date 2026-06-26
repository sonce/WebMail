using WebMail.Domain;

namespace WebMail.Services;

public sealed class BuyerRuleService
{
    private static readonly HashSet<EmailAuthorizationStatus> BuyerUnlinkAllowed =
    [EmailAuthorizationStatus.NotAuthorized, EmailAuthorizationStatus.PendingReview, EmailAuthorizationStatus.Rejected];

    public bool CanBuyerUnlink(EmailAuthorizationStatus status) => BuyerUnlinkAllowed.Contains(status);
    public string BuyerUnlinkBlockedMessage => "正在处理中，无法删除";

    public bool CanSalesDeleteBuyer(Buyer buyer, long salesUserId) =>
        !buyer.IsDeleted && buyer.SaleId == salesUserId && BuyerUnlinkAllowed.Contains(buyer.EmailStatus);

    public bool CanSupplierViewBuyer(Buyer buyer, long? assignedSupplierId, long currentSupplierId) =>
        !buyer.IsDeleted && buyer.EmailStatus == EmailAuthorizationStatus.Normal && assignedSupplierId == currentSupplierId;
}
