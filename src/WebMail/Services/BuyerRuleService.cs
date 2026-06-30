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
        && !(buyer.ReviewStatus == ReviewStatus.Approved && buyer.SupplierStatus == SupplierProcessingStatus.Unprocessed);

    public bool CanSupplierViewBuyer(Buyer buyer, long? assignedSupplierId, long currentSupplierId) =>
        !buyer.IsDeleted
        && buyer.ReviewStatus == ReviewStatus.Approved
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

        return buyer.Stage switch
        {
            BuyerStage.NotSubmitted => BuyerMailAction.Authorize,
            BuyerStage.Submitted => buyer.ReviewStatus switch
            {
                ReviewStatus.Pending or ReviewStatus.Rejected => BuyerMailAction.ChangeEmail | BuyerMailAction.ClearAuth,
                ReviewStatus.Approved => buyer.SupplierStatus switch
                {
                    SupplierProcessingStatus.Failed => BuyerMailAction.ChangeEmail,
                    SupplierProcessingStatus.Completed => buyer.EmailStatus == EmailAuthorizationStatus.Authorized
                        ? BuyerMailAction.ClearAuth
                        : BuyerMailAction.None,
                    _ => BuyerMailAction.None
                },
                _ => BuyerMailAction.None
            },
            _ => BuyerMailAction.None
        };
    }

    public bool CanSupplierSetStatus(Buyer buyer, long? assignedSupplierId, long currentSupplierId) =>
        CanSupplierViewBuyer(buyer, assignedSupplierId, currentSupplierId);
}
