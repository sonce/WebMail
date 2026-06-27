using WebMail.Domain;
using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class BuyerRuleServiceTests
{
    private readonly BuyerRuleService _service = new();

    [Theory]
    [InlineData(BuyerStatus.NotSubmitted, EmailAuthorizationStatus.NotAuthorized, true)]
    [InlineData(BuyerStatus.PendingReview, EmailAuthorizationStatus.Authorized, true)]
    [InlineData(BuyerStatus.Rejected, EmailAuthorizationStatus.Authorized, true)]
    [InlineData(BuyerStatus.Approved, EmailAuthorizationStatus.Authorized, false)]
    [InlineData(BuyerStatus.PendingReview, EmailAuthorizationStatus.Abnormal, false)]
    public void BuyerCanUnlinkOnlyBeforeApproval(BuyerStatus buyerStatus, EmailAuthorizationStatus emailStatus, bool expected)
    {
        var buyer = new Buyer { BuyerStatus = buyerStatus, EmailStatus = emailStatus };
        Assert.Equal(expected, _service.CanBuyerUnlink(buyer));
    }

    [Fact]
    public void SalesCannotDeleteOtherSalesBuyer()
    {
        var buyer = new Buyer { SaleId = 10, BuyerStatus = BuyerStatus.PendingReview, EmailStatus = EmailAuthorizationStatus.Authorized };
        Assert.False(_service.CanSalesDeleteBuyer(buyer, 99));
    }

    [Fact]
    public void SupplierCanSeeOnlyAssignedApprovedAuthorizedBuyer()
    {
        var buyer = new Buyer { BuyerStatus = BuyerStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized };
        Assert.True(_service.CanSupplierViewBuyer(buyer, 7, 7));
    }

    private static Buyer B(EmailAuthorizationStatus email, BuyerStatus buyer, SupplierProcessingStatus supplier = SupplierProcessingStatus.Unprocessed) =>
        new() { EmailStatus = email, BuyerStatus = buyer, SupplierStatus = supplier };

    [Fact]
    public void Action_NotSubmitted_AllowsAuthorize() =>
        Assert.Equal(BuyerMailAction.Authorize,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.NotAuthorized, BuyerStatus.NotSubmitted)));

    [Theory]
    [InlineData(BuyerStatus.PendingReview)]
    [InlineData(BuyerStatus.Rejected)]
    public void Action_PreApproval_AllowsChangeAndClear(BuyerStatus status) =>
        Assert.Equal(BuyerMailAction.ChangeEmail | BuyerMailAction.ClearAuth,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.Authorized, status)));

    [Fact]
    public void Action_Abnormal_AllowsReauthAndChange() =>
        Assert.Equal(BuyerMailAction.ReAuthorize | BuyerMailAction.ChangeEmail,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.Abnormal, BuyerStatus.Approved, SupplierProcessingStatus.Failed)));

    [Fact]
    public void Action_ApprovedUnprocessed_IsLocked() =>
        Assert.Equal(BuyerMailAction.None,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.Authorized, BuyerStatus.Approved, SupplierProcessingStatus.Unprocessed)));

    [Fact]
    public void Action_ApprovedFailed_AllowsChangeEmail() =>
        Assert.Equal(BuyerMailAction.ChangeEmail,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.Authorized, BuyerStatus.Approved, SupplierProcessingStatus.Failed)));

    [Fact]
    public void Action_ApprovedCompleted_AllowsClearThenTerminal()
    {
        Assert.Equal(BuyerMailAction.ClearAuth,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.Authorized, BuyerStatus.Approved, SupplierProcessingStatus.Completed)));
        Assert.Equal(BuyerMailAction.None,
            _service.ResolveBuyerMailAction(B(EmailAuthorizationStatus.NotAuthorized, BuyerStatus.Approved, SupplierProcessingStatus.Completed)));
    }

    [Theory]
    [InlineData(BuyerStatus.NotSubmitted, EmailAuthorizationStatus.NotAuthorized, SupplierProcessingStatus.Unprocessed, true)]
    [InlineData(BuyerStatus.Approved, EmailAuthorizationStatus.Authorized, SupplierProcessingStatus.Failed, true)]
    [InlineData(BuyerStatus.Approved, EmailAuthorizationStatus.Authorized, SupplierProcessingStatus.Completed, true)]
    [InlineData(BuyerStatus.Approved, EmailAuthorizationStatus.Authorized, SupplierProcessingStatus.Unprocessed, false)]
    [InlineData(BuyerStatus.PendingReview, EmailAuthorizationStatus.Abnormal, SupplierProcessingStatus.Unprocessed, false)]
    public void SalesDelete_FollowsLifecycle(BuyerStatus bs, EmailAuthorizationStatus es, SupplierProcessingStatus ss, bool expected)
    {
        var buyer = new Buyer { SaleId = 5, BuyerStatus = bs, EmailStatus = es, SupplierStatus = ss };
        Assert.Equal(expected, _service.CanSalesDeleteBuyer(buyer, 5));
    }

    [Fact]
    public void SupplierSetStatus_OnlyApprovedAuthorizedAssigned()
    {
        var ok = new Buyer { BuyerStatus = BuyerStatus.Approved, EmailStatus = EmailAuthorizationStatus.Authorized };
        Assert.True(_service.CanSupplierSetStatus(ok, 3, 3));
        var notApproved = new Buyer { BuyerStatus = BuyerStatus.PendingReview, EmailStatus = EmailAuthorizationStatus.Authorized };
        Assert.False(_service.CanSupplierSetStatus(notApproved, 3, 3));
    }
}
