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
}
