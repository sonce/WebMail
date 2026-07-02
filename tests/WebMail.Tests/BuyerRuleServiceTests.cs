using WebMail.Domain;
using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class BuyerRuleServiceTests
{
    private readonly BuyerRuleService _service = new();

    [Fact]
    public void SalesCannotDeleteOtherSalesBuyer()
    {
        var buyer = new Buyer { SaleId = 10, Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Pending, EmailStatus = EmailAuthorizationStatus.Authorized };
        Assert.False(_service.CanSalesDeleteBuyer(buyer, 99));
    }

    [Fact]
    public void SupplierCanSeeOnlyAssignedApprovedAuthorizedBuyer()
    {
        var buyer = new Buyer { Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized };
        Assert.True(_service.CanSupplierViewBuyer(buyer, 7, 7));
    }

    private static Buyer B(EmailAuthorizationStatus email, BuyerStage stage, ReviewStatus review = ReviewStatus.Pending, SupplierProcessingStatus supplier = SupplierProcessingStatus.Unprocessed) =>
        new() { EmailStatus = email, Stage = stage, ReviewStatus = review, SupplierStatus = supplier };

    [Fact]
    public void Action_Opened_AllowsAuthorize() =>
        Assert.Equal(BuyerMailAction.Authorize,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.NotAuthorized, BuyerStage.Opened)));

    [Theory]
    [InlineData(ReviewStatus.Pending)]
    [InlineData(ReviewStatus.Rejected)]
    public void Action_PreApproval_AllowsChangeAndClear(ReviewStatus review) =>
        Assert.Equal(BuyerMailAction.ChangeEmail | BuyerMailAction.ClearAuth,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.Authorized, BuyerStage.Submitted, review)));

    [Fact]
    public void Action_Abnormal_AllowsReauthAndChange() =>
        Assert.Equal(BuyerMailAction.ReAuthorize | BuyerMailAction.ChangeEmail,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.Abnormal, BuyerStage.Submitted, ReviewStatus.Approved, SupplierProcessingStatus.Failed)));

    [Fact]
    public void Action_ApprovedUnprocessed_IsLocked() =>
        Assert.Equal(BuyerMailAction.None,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.Authorized, BuyerStage.Submitted, ReviewStatus.Approved, SupplierProcessingStatus.Unprocessed)));

    [Fact]
    public void Action_ApprovedFailed_AllowsChangeEmail() =>
        Assert.Equal(BuyerMailAction.ChangeEmail,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.Authorized, BuyerStage.Submitted, ReviewStatus.Approved, SupplierProcessingStatus.Failed)));

    [Fact]
    public void Action_ApprovedCompleted_AllowsClearThenTerminal()
    {
        Assert.Equal(BuyerMailAction.ClearAuth,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.Authorized, BuyerStage.Submitted, ReviewStatus.Approved, SupplierProcessingStatus.Completed)));
        Assert.Equal(BuyerMailAction.None,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.NotAuthorized, BuyerStage.Submitted, ReviewStatus.Approved, SupplierProcessingStatus.Completed)));
    }

    [Theory]
    [InlineData(BuyerStage.Opened, ReviewStatus.Pending, EmailAuthorizationStatus.NotAuthorized, SupplierProcessingStatus.Unprocessed, true)]
    [InlineData(BuyerStage.Submitted, ReviewStatus.Approved, EmailAuthorizationStatus.Authorized, SupplierProcessingStatus.Failed, true)]
    [InlineData(BuyerStage.Submitted, ReviewStatus.Approved, EmailAuthorizationStatus.Authorized, SupplierProcessingStatus.Completed, true)]
    [InlineData(BuyerStage.Submitted, ReviewStatus.Approved, EmailAuthorizationStatus.Authorized, SupplierProcessingStatus.Unprocessed, false)]
    [InlineData(BuyerStage.Submitted, ReviewStatus.Pending, EmailAuthorizationStatus.Abnormal, SupplierProcessingStatus.Unprocessed, false)]
    public void SalesDelete_FollowsLifecycle(BuyerStage stage, ReviewStatus review, EmailAuthorizationStatus es, SupplierProcessingStatus ss, bool expected)
    {
        var buyer = new Buyer { SaleId = 5, Stage = stage, ReviewStatus = review, EmailStatus = es, SupplierStatus = ss };
        Assert.Equal(expected, _service.CanSalesDeleteBuyer(buyer, 5));
    }

    [Fact]
    public void SupplierSetStatus_OnlyApprovedAuthorizedAssigned()
    {
        var ok = new Buyer { Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized };
        Assert.True(_service.CanSupplierSetStatus(ok, 3, 3));
        var notApproved = new Buyer { Stage = BuyerStage.Submitted, ReviewStatus = ReviewStatus.Pending, EmailStatus = EmailAuthorizationStatus.Authorized };
        Assert.False(_service.CanSupplierSetStatus(notApproved, 3, 3));
    }
}
