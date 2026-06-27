using WebMail.Domain;

namespace WebMail.Services;

public sealed class BuyerRuleService
{
    private static readonly HashSet<BuyerStatus> PreApprovalStatuses =
        [BuyerStatus.NotSubmitted, BuyerStatus.PendingReview, BuyerStatus.Rejected];

    public bool CanBuyerUnlink(Buyer buyer) =>
        buyer.EmailStatus != EmailAuthorizationStatus.Abnormal
        && PreApprovalStatuses.Contains(buyer.BuyerStatus);

    public string BuyerUnlinkBlockedMessage => "正在处理中，无法删除";

    public bool CanSalesDeleteBuyer(Buyer buyer, long salesUserId) =>
        !buyer.IsDeleted
        && buyer.SaleId == salesUserId
        && buyer.EmailStatus != EmailAuthorizationStatus.Abnormal
        && PreApprovalStatuses.Contains(buyer.BuyerStatus);

    public bool CanSupplierViewBuyer(Buyer buyer, long? assignedSupplierId, long currentSupplierId) =>
        !buyer.IsDeleted
        && buyer.BuyerStatus == BuyerStatus.Approved
        && buyer.EmailStatus == EmailAuthorizationStatus.Authorized
        && assignedSupplierId == currentSupplierId;
}
