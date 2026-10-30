namespace Mooncake.EcommercePlatform.Verification.Tests;

using Moq;
using Xunit;
using Mooncake.EcommercePlatform.Application.Common.Exceptions;
using Mooncake.EcommercePlatform.Application.Common.Interfaces;
using Mooncake.EcommercePlatform.Application.DTOs.Admin;
using Mooncake.EcommercePlatform.Application.DTOs.Orders.Requests;
using Mooncake.EcommercePlatform.Application.DTOs.Promotions.Requests;
using Mooncake.EcommercePlatform.Application.DTOs.Rfqs.Requests;
using Mooncake.EcommercePlatform.Application.Services.Implementations;
using Mooncake.EcommercePlatform.Domain.Entities;
using Mooncake.EcommercePlatform.Domain.Enums;

public sealed class MoneyConsistencyTests
{
    [Theory]
    [InlineData(DiscountType.Percentage, false)]
    [InlineData(DiscountType.FixedAmount, false)]
    [InlineData(DiscountType.FixedPrice, false)]
    [InlineData(DiscountType.FixedAmount, true)]
    public async Task PromotionRejectsFractionalCentsBeforeSaving(DiscountType type, bool update)
    {
        var repository = new Mock<IPromotionRepository>();
        var shop = new Shop { Id = Guid.NewGuid() };
        repository.Setup(x => x.GetVerifiedShopAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(shop);
        repository.Setup(x => x.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PromotionRule { ShopId = shop.Id });
        var service = new PromotionService(repository.Object, Mock.Of<IDateTimeProvider>());
        var request = new SavePromotionRequest { Name = "Promotion", MinQty = 1, DiscountType = type, DiscountValue = 1.005m };
        var error = await Assert.ThrowsAsync<HttpException>(() => update
            ? service.UpdateAsync(Guid.NewGuid(), Guid.NewGuid(), request)
            : service.CreateAsync(Guid.NewGuid(), request));
        Assert.Equal(400, error.StatusCode);
        repository.Verify(x => x.SaveAsync(It.IsAny<PromotionRule>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DepositRuleRejectsPrecisionLoss(bool percentage)
    {
        var repository = new Mock<IAdminPlatformRepository>(MockBehavior.Strict);
        var service = new AdminPlatformService(repository.Object, Mock.Of<IUserRepository>(), Mock.Of<IDateTimeProvider>(), Mock.Of<ISupplierRepository>());
        var error = await Assert.ThrowsAsync<HttpException>(() => service.SaveContractDepositRuleAsync(null, new SaveContractDepositRuleRequest {
            Name = "Deposit", DepositPercentage = percentage ? 30.005m : 30m, MinContractValue = percentage ? null : 1.005m
        }, Guid.NewGuid(), null));
        Assert.Equal(400, error.StatusCode);
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("budgetMin")]
    [InlineData("budgetMax")]
    [InlineData("target")]
    public async Task RfqRejectsFractionalCentsInOptionalAmounts(string field)
    {
        var repository = new Mock<IRfqRepository>(MockBehavior.Strict);
        var service = new RfqService(repository.Object, Mock.Of<IDateTimeProvider>());
        var error = await Assert.ThrowsAsync<HttpException>(() => service.CreateAsync(Guid.NewGuid(), new() {
            Title = "Mooncakes", DeliveryAddress = "Address", BidDeadline = DateTime.UtcNow.AddDays(1),
            DeliveryDateRequired = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)),
            BudgetRangeMin = field == "budgetMin" ? 1.005m : null, BudgetRangeMax = field == "budgetMax" ? 1.005m : null,
            Items = [new() { ProductName = "Mooncake", Unit = "piece", Quantity = 1, TargetPrice = field == "target" ? 1.005m : null }]
        }));
        Assert.Equal(400, error.StatusCode);
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderRoundsDiscountedUnitPriceAndRejectsOversizedSubtotal(bool oversized)
    {
        var repository = new Mock<IOrderRepository>();
        var shop = new Shop { Id = Guid.NewGuid(), Status = ShopStatus.Active };
        var product = new Product { Id = Guid.NewGuid(), ShopId = shop.Id, Name = "Cake", BasePrice = oversized ? 9_999_999_999.99m : 1.01m, MinOrderQty = 1, Status = ProductStatus.Active };
        var variant = new ProductVariant { Id = Guid.NewGuid(), ProductId = product.Id, Name = "Box", Sku = "BOX", StockQty = 10 };
        repository.Setup(x => x.GetShopAsync(shop.Id, It.IsAny<CancellationToken>())).ReturnsAsync(shop);
        repository.Setup(x => x.GetProductsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<Product> { product });
        repository.Setup(x => x.GetVariantsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<ProductVariant> { variant });
        repository.Setup(x => x.GetActivePromotionsAsync(shop.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new List<PromotionRule> {
            new() { DiscountType = DiscountType.Percentage, DiscountValue = 50m } });
        Order? saved = null;
        IReadOnlyList<OrderItem> lines = [];
        repository.Setup(x => x.CreateOrderAsync(It.IsAny<Order>(), It.IsAny<IReadOnlyList<OrderItem>>(), It.IsAny<CancellationToken>()))
            .Callback<Order, IReadOnlyList<OrderItem>, CancellationToken>((order, items, _) => { saved = order; lines = items; }).ReturnsAsync(true);
        var service = new OrderService(repository.Object, Mock.Of<IDateTimeProvider>());
        var request = new CreateOrderRequest { ShopId = shop.Id, DeliveryAddress = "Address", RecipientName = "Customer", RecipientPhone = "0901234567",
            Items = [new() { ProductId = product.Id, VariantId = variant.Id, Quantity = 3 }] };
        if (oversized)
        {
            var error = await Assert.ThrowsAsync<HttpException>(() => service.CreateAsync(Guid.NewGuid(), "money-test-key", request));
            Assert.Equal(400, error.StatusCode);
            Assert.Null(saved);
            return;
        }
        var response = await service.CreateAsync(Guid.NewGuid(), "money-test-key", request);
        Assert.Equal(3.03m, saved!.Subtotal);
        Assert.Equal(1.50m, saved.DiscountAmount);
        Assert.Equal(1.53m, saved.TotalAmount);
        Assert.Equal(lines.Sum(x => x.Subtotal), response.TotalAmount);
        Assert.Equal(lines.Sum(x => x.UnitPrice * x.Quantity - x.DiscountAmount), response.TotalAmount);
    }

    [Theory]
    [InlineData("1.005", 3)]
    [InlineData("10000000000", 1)]
    [InlineData("9999999999.99", 2)]
    public async Task BidRejectsAmountsThatCannotBeStoredExactly(string price, int quantity)
    {
        var (service, repository, itemId) = BidSetup(quantity);
        var request = new SubmitBidRequest { Items = [new() { RfqItemId = itemId,
            UnitPrice = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture) }] };

        var error = await Assert.ThrowsAsync<HttpException>(() => service.SubmitBidAsync(Guid.NewGuid(), Guid.NewGuid(), request));

        Assert.Equal(400, error.StatusCode);
        repository.Verify(x => x.CreateBidAsync(It.IsAny<Bid>(), It.IsAny<IReadOnlyList<BidItem>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("1.01", 3, "3.03")]
    [InlineData("1.0100", 3, "3.03")]
    [InlineData("9999999999.99", 1, "9999999999.99")]
    public async Task BidPersistsMatchingUnitPriceLineAndTotal(string price, int quantity, string expected)
    {
        var (service, repository, itemId) = BidSetup(quantity);
        Bid? saved = null;
        IReadOnlyList<BidItem> lines = [];
        repository.Setup(x => x.CreateBidAsync(It.IsAny<Bid>(), It.IsAny<IReadOnlyList<BidItem>>(), It.IsAny<CancellationToken>()))
            .Callback<Bid, IReadOnlyList<BidItem>, CancellationToken>((bid, items, _) => { saved = bid; lines = items; }).ReturnsAsync(true);
        repository.Setup(x => x.GetBidItemsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => lines);

        var response = await service.SubmitBidAsync(Guid.NewGuid(), Guid.NewGuid(), new() { Items = [new() {
            RfqItemId = itemId, UnitPrice = decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture) }] });

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), saved!.TotalPrice);
        Assert.Equal(lines.Sum(x => x.UnitPrice * x.Quantity), saved.TotalPrice);
        Assert.Equal(saved.TotalPrice, response.TotalPrice);
    }

    private static (RfqService, Mock<IRfqRepository>, Guid) BidSetup(int quantity)
    {
        var repository = new Mock<IRfqRepository>();
        var clock = new Mock<IDateTimeProvider>();
        var now = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc);
        clock.SetupGet(x => x.UtcNow).Returns(now);
        repository.Setup(x => x.GetVerifiedSupplierProfileIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
        repository.Setup(x => x.IsActiveSupplierAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        repository.Setup(x => x.GetRfqAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RequestForQuotation { Status = RfqStatus.Open, BidDeadline = now.AddDays(1) });
        repository.Setup(x => x.GetInvitationAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RfqInvitation { Status = RfqInvitationStatus.Invited });
        var itemId = Guid.NewGuid();
        repository.Setup(x => x.GetRfqItemsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RfqItem> { new() { Id = itemId, Quantity = quantity } });
        return (new RfqService(repository.Object, clock.Object), repository, itemId);
    }
}
