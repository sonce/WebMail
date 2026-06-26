using WebMail.Domain;
using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class BuyerRuleServiceTests
{
    private readonly BuyerRuleService _service = new();

    [Theory]
    [InlineData(EmailAuthorizationStatus.NotAuthorized, true)]
    [InlineData(EmailAuthorizationStatus.PendingReview, true)]
    [InlineData(EmailAuthorizationStatus.Rejected, true)]
    [InlineData(EmailAuthorizationStatus.Normal, false)]
    [InlineData(EmailAuthorizationStatus.Abnormal, false)]
    public void BuyerCanUnlinkOnlyBeforeProcessing(EmailAuthorizationStatus status, bool expected) => Assert.Equal(expected, _service.CanBuyerUnlink(status));

    [Fact]
    public void SalesCannotDeleteOtherSalesBuyer()
    {
        var buyer = new Buyer { SaleId = 10, EmailStatus = EmailAuthorizationStatus.PendingReview };
        Assert.False(_service.CanSalesDeleteBuyer(buyer, 99));
    }

    [Fact]
    public void SupplierCanSeeOnlyAssignedNormalBuyer()
    {
        var buyer = new Buyer { EmailStatus = EmailAuthorizationStatus.Normal };
        Assert.True(_service.CanSupplierViewBuyer(buyer, 7, 7));
    }
}
