using WebMail.Domain;

namespace WebMail.Services;

[Flags]
public enum BuyerMailAction
{
    None = 0,
    Authorize = 1,
    ReAuthorize = 2,
    ChangeEmail = 4,
    ClearAuth = 8
}

public sealed class BuyerRuleService
{
    public string BuyerUnlinkBlockedMessageKey => "Buyer.UnlinkBlocked";

    public bool CanSalesDeleteBuyer(Buyer buyer, long salesUserId) =>
        !buyer.IsDeleted
        && buyer.SaleId == salesUserId
        && buyer.EmailStatus != EmailAuthorizationStatus.Abnormal
        && !(buyer.BuyerStatus == BuyerStatus.Approved && buyer.SupplierStatus == SupplierProcessingStatus.Unprocessed);

    public bool CanSupplierViewBuyer(Buyer buyer, long? assignedSupplierId, long currentSupplierId) =>
        !buyer.IsDeleted
        && buyer.BuyerStatus == BuyerStatus.Approved
        && buyer.EmailStatus == EmailAuthorizationStatus.Authorized
        && assignedSupplierId == currentSupplierId;

    public BuyerMailAction ResolveBuyerMailAction(Buyer buyer)
    {
        if (buyer.IsDeleted)
        {
            return BuyerMailAction.None;
        }

        if (buyer.EmailStatus == EmailAuthorizationStatus.Abnormal)
        {
            return BuyerMailAction.ReAuthorize | BuyerMailAction.ChangeEmail;
        }

        return buyer.BuyerStatus switch
        {
            BuyerStatus.NotSubmitted => BuyerMailAction.Authorize,
            BuyerStatus.PendingReview or BuyerStatus.Rejected => BuyerMailAction.ChangeEmail | BuyerMailAction.ClearAuth,
            BuyerStatus.Approved => buyer.SupplierStatus switch
            {
                SupplierProcessingStatus.Failed => BuyerMailAction.ChangeEmail,
                SupplierProcessingStatus.Completed => buyer.EmailStatus == EmailAuthorizationStatus.Authorized
                    ? BuyerMailAction.ClearAuth
                    : BuyerMailAction.None,
                _ => BuyerMailAction.None
            },
            _ => BuyerMailAction.None
        };
    }

    public bool CanSupplierSetStatus(Buyer buyer, long? assignedSupplierId, long currentSupplierId) =>
        CanSupplierViewBuyer(buyer, assignedSupplierId, currentSupplierId);
}
