namespace Mooncake.EcommercePlatform.Application.Services.Implementations;

using Mooncake.EcommercePlatform.Application.Common.Exceptions;
using Mooncake.EcommercePlatform.Application.Common.Helpers;
using Mooncake.EcommercePlatform.Application.Common.Interfaces;
using Mooncake.EcommercePlatform.Application.DTOs.Orders.Requests;
using Mooncake.EcommercePlatform.Application.DTOs.Orders.Responses;
using Mooncake.EcommercePlatform.Application.Services.Interfaces;
using Mooncake.EcommercePlatform.Domain.Entities;
using Mooncake.EcommercePlatform.Domain.Enums;
using System.Text.RegularExpressions;

/// <summary>Implements bulk ordering and order status transitions.</summary>
public sealed class OrderService(IOrderRepository repository, IDateTimeProvider dateTimeProvider) : IOrderService
{
    public async Task<OrderResponse> CreateAsync(Guid customerId, string? idempotencyKey, CreateOrderRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new HttpException(400, "Idempotency-Key header is required.");
        var normalizedIdempotencyKey = idempotencyKey.Trim();
        if (!Regex.IsMatch(normalizedIdempotencyKey, "^[A-Za-z0-9._:-]{8,128}$", RegexOptions.CultureInvariant))
            throw new HttpException(400, "Idempotency-Key must contain 8 to 128 letters, digits, dots, underscores, colons, or hyphens.");
        var requestHash = IdempotencyRequestHelper.CreateHash(request);
        var previousOrder = await repository.GetByIdempotencyKeyAsync(customerId, normalizedIdempotencyKey, cancellationToken);
        if (previousOrder is not null)
        {
            if (!string.Equals(previousOrder.IdempotencyRequestHash, requestHash, StringComparison.Ordinal))
                throw new HttpException(409, "This Idempotency-Key was already used with a different order request.");
            return await ToResponseAsync(previousOrder, cancellationToken);
        }

        if (request.Items.Select(item => item.VariantId).Distinct().Count() != request.Items.Count)
            throw new HttpException(400, "Each product variant can appear only once in an order.");
        if (request.DeliveryDateExpected < DateOnly.FromDateTime(dateTimeProvider.UtcNow))
            throw new HttpException(400, "Expected delivery date cannot be in the past.");

        var shop = await repository.GetShopAsync(request.ShopId, cancellationToken)
                   ?? throw new HttpException(404, "Shop was not found.");
        if (shop.Status != ShopStatus.Active)
            throw new HttpException(409, "The shop is not accepting orders.");

        var productIds = request.Items.Select(item => item.ProductId).Distinct().ToArray();
        var variantIds = request.Items.Select(item => item.VariantId).Distinct().ToArray();
        var products = await repository.GetProductsAsync(productIds, cancellationToken);
        var variants = await repository.GetVariantsAsync(variantIds, cancellationToken);
        if (products.Count != productIds.Length || variants.Count != variantIds.Length)
            throw new HttpException(400, "One or more selected products or variants are unavailable.");

        var productsById = products.ToDictionary(product => product.Id);
        var variantsById = variants.ToDictionary(variant => variant.Id);
        var promotions = await repository.GetActivePromotionsAsync(shop.Id, cancellationToken);
        var now = dateTimeProvider.UtcNow;
        var items = new List<OrderItem>(request.Items.Count);
        decimal subtotal = 0;
        decimal discountTotal = 0;

        foreach (var requestedItem in request.Items)
        {
            var product = productsById[requestedItem.ProductId];
            var variant = variantsById[requestedItem.VariantId];
            if (product.ShopId != shop.Id || variant.ProductId != product.Id || product.Status != ProductStatus.Active || product.IsDeleted || variant.IsDeleted)
                throw new HttpException(400, "All ordered variants must belong to active products in the selected shop.");
            if (requestedItem.Quantity < product.MinOrderQty || product.MaxOrderQty is int maxQuantity && requestedItem.Quantity > maxQuantity)
                throw new HttpException(400, $"Quantity for '{product.Name}' is outside the allowed order range.");
            if (variant.StockQty < requestedItem.Quantity)
                throw new HttpException(409, $"Insufficient stock for variant '{variant.Sku}'.");

            var unitPrice = product.BasePrice + variant.PriceAdjustment;
            MoneyHelper.RequireAmount(unitPrice, 0.01m);
            var lineTotal = decimal.Round(unitPrice * requestedItem.Quantity, 2, MidpointRounding.AwayFromZero);
            var discountedUnitPrice = GetDiscountedUnitPrice(unitPrice, requestedItem.Quantity, product.Id, promotions, now);
            var lineDiscount = decimal.Round((unitPrice - discountedUnitPrice) * requestedItem.Quantity, 2, MidpointRounding.AwayFromZero);
            subtotal += lineTotal;
            discountTotal += lineDiscount;
            items.Add(new OrderItem
            {
                ProductId = product.Id,
                VariantId = variant.Id,
                ProductNameSnapshot = product.Name,
                VariantNameSnapshot = variant.Name,
                VariantSkuSnapshot = variant.Sku,
                Quantity = requestedItem.Quantity,
                UnitPrice = unitPrice,
                DiscountAmount = lineDiscount,
                Subtotal = lineTotal - lineDiscount
            });
        }

        subtotal = decimal.Round(subtotal, 2, MidpointRounding.AwayFromZero);
        discountTotal = decimal.Round(discountTotal, 2, MidpointRounding.AwayFromZero);
        MoneyHelper.RequireAmount(subtotal);
        MoneyHelper.RequireAmount(discountTotal);
        var order = new Order
        {
            CustomerId = customerId,
            ShopId = shop.Id,
            IdempotencyKey = normalizedIdempotencyKey,
            IdempotencyRequestHash = requestHash,
            OrderNumber = $"MC-{now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..28].ToUpperInvariant(),
            Subtotal = subtotal,
            DiscountAmount = discountTotal,
            TaxAmount = 0,
            ShippingFee = 0,
            TotalAmount = subtotal - discountTotal,
            Notes = request.Notes?.Trim(),
            DeliveryAddress = request.DeliveryAddress.Trim(),
            RecipientName = request.RecipientName.Trim(),
            RecipientPhone = request.RecipientPhone.Trim(),
            DeliveryDateExpected = request.DeliveryDateExpected,
            Status = OrderStatus.Pending,
            CreatedBy = customerId
        };
        if (!await repository.CreateOrderAsync(order, items, cancellationToken))
        {
            previousOrder = await repository.GetByIdempotencyKeyAsync(customerId, normalizedIdempotencyKey, cancellationToken);
            if (previousOrder is not null)
            {
                if (!string.Equals(previousOrder.IdempotencyRequestHash, requestHash, StringComparison.Ordinal))
                    throw new HttpException(409, "This Idempotency-Key was already used with a different order request.");
                return await ToResponseAsync(previousOrder, cancellationToken);
            }
            throw new HttpException(409, "Inventory changed while placing the order. Review the quantities and try again.");
        }
        return OrderResponseHelper.ToResponse(order, items);
    }

    public async Task<IReadOnlyList<OrderResponse>> GetMyOrdersAsync(Guid userId, bool supplier, CancellationToken cancellationToken = default)
    {
        var orders = supplier
            ? await repository.GetSupplierOrdersAsync(userId, cancellationToken)
            : await repository.GetCustomerOrdersAsync(userId, cancellationToken);
        var results = new List<OrderResponse>(orders.Count);
        foreach (var order in orders)
            results.Add(await ToResponseAsync(order, cancellationToken));
        return results;
    }

    public async Task<OrderResponse> GetAsync(Guid userId, bool supplier, Guid orderId, bool admin = false, CancellationToken cancellationToken = default)
    {
        var order = await repository.GetOrderAsync(orderId, cancellationToken)
                    ?? throw new HttpException(404, "Order was not found.");
        var authorized = admin || (supplier
            ? await repository.IsShopOwnedByUserAsync(order.ShopId, userId, cancellationToken)
            : order.CustomerId == userId);
        if (!authorized)
            throw new HttpException(404, "Order was not found.");
        return await ToResponseAsync(order, cancellationToken);
    }

    public async Task<OrderResponse> CancelAsync(Guid customerId, Guid orderId, CancelOrderRequest request, CancellationToken cancellationToken = default)
    {
        var order = await repository.GetOrderAsync(orderId, cancellationToken)
                    ?? throw new HttpException(404, "Order was not found.");
        if (order.CustomerId != customerId)
            throw new HttpException(404, "Order was not found.");
        if (order.Status != OrderStatus.Pending)
            throw new HttpException(409, "Only pending orders can be cancelled.");
        if (!await repository.CancelPendingOrderAsync(order, customerId, request.Reason.Trim(), cancellationToken))
            throw new HttpException(409, "The order status changed and it can no longer be cancelled.");
        return await ToResponseAsync(order, cancellationToken);
    }

    public async Task<OrderResponse> ConfirmAsync(Guid supplierUserId, Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await repository.GetOrderAsync(orderId, cancellationToken)
                    ?? throw new HttpException(404, "Order was not found.");
        if (!await repository.IsShopOwnedByUserAsync(order.ShopId, supplierUserId, cancellationToken))
            throw new HttpException(404, "Order was not found.");
        if (order.Status != OrderStatus.Pending)
            throw new HttpException(409, "Only pending orders can be confirmed.");
        if (!await repository.ConfirmPendingOrderAsync(order, supplierUserId, cancellationToken))
            throw new HttpException(409, "The order status changed and it can no longer be confirmed.");
        return await ToResponseAsync(order, cancellationToken);
    }

    public async Task<OrderResponse> RejectAsync(Guid supplierUserId, Guid orderId, CancelOrderRequest request, CancellationToken cancellationToken = default)
    {
        var order = await repository.GetOrderAsync(orderId, cancellationToken)
                    ?? throw new HttpException(404, "Order was not found.");
        if (!await repository.IsShopOwnedByUserAsync(order.ShopId, supplierUserId, cancellationToken))
            throw new HttpException(404, "Order was not found.");
        if (order.Status != OrderStatus.Pending)
            throw new HttpException(409, "Only pending orders can be rejected.");
        if (!await repository.CancelPendingOrderAsync(order, supplierUserId, $"Supplier rejected: {request.Reason.Trim()}", cancellationToken))
            throw new HttpException(409, "The order status changed and it can no longer be rejected.");
        return await ToResponseAsync(order, cancellationToken);
    }

    public Task<OrderResponse> StartProcessingAsync(Guid supplierUserId, Guid orderId, CancellationToken cancellationToken = default) =>
        TransitionAsSupplierAsync(supplierUserId, orderId, OrderStatus.Confirmed, OrderStatus.Processing, cancellationToken);

    public Task<OrderResponse> MarkShippedAsync(Guid supplierUserId, Guid orderId, CancellationToken cancellationToken = default) =>
        TransitionAsSupplierAsync(supplierUserId, orderId, OrderStatus.Processing, OrderStatus.Shipped, cancellationToken);

    public async Task<OrderResponse> ConfirmDeliveryAsync(Guid customerId, Guid orderId, CancellationToken cancellationToken = default)
    {
        var order = await repository.GetOrderAsync(orderId, cancellationToken)
                    ?? throw new HttpException(404, "Order was not found.");
        if (order.CustomerId != customerId) throw new HttpException(404, "Order was not found.");
        if (order.Status != OrderStatus.Shipped) throw new HttpException(409, "Only shipped orders can be marked delivered.");
        if (!await repository.TransitionOrderAsync(orderId, OrderStatus.Shipped, OrderStatus.Delivered, customerId, cancellationToken))
            throw new HttpException(409, "The order status changed and delivery could not be confirmed.");
        order.Status = OrderStatus.Delivered;
        return await ToResponseAsync(order, cancellationToken);
    }

    private async Task<OrderResponse> TransitionAsSupplierAsync(Guid supplierUserId, Guid orderId, OrderStatus expectedStatus,
        OrderStatus nextStatus, CancellationToken cancellationToken)
    {
        var order = await repository.GetOrderAsync(orderId, cancellationToken)
                    ?? throw new HttpException(404, "Order was not found.");
        if (!await repository.IsShopOwnedByUserAsync(order.ShopId, supplierUserId, cancellationToken))
            throw new HttpException(404, "Order was not found.");
        if (order.Status != expectedStatus) throw new HttpException(409, $"Only {expectedStatus} orders can move to {nextStatus}.");
        if (!await repository.TransitionOrderAsync(orderId, expectedStatus, nextStatus, supplierUserId, cancellationToken))
            throw new HttpException(409, "The order status changed and could not be updated.");
        order.Status = nextStatus;
        return await ToResponseAsync(order, cancellationToken);
    }

    private async Task<OrderResponse> ToResponseAsync(Order order, CancellationToken cancellationToken) =>
        OrderResponseHelper.ToResponse(order, await repository.GetOrderItemsAsync(order.Id, cancellationToken));

    private static decimal GetDiscountedUnitPrice(decimal unitPrice, int quantity, Guid productId, IReadOnlyList<PromotionRule> rules, DateTime now)
    {
        var discountedPrice = unitPrice;
        foreach (var rule in rules)
        {
            if (rule.ProductId.HasValue && rule.ProductId.Value != productId)
                continue;
            if (rule.MinQty.HasValue && quantity < rule.MinQty.Value || rule.MaxQty.HasValue && quantity > rule.MaxQty.Value)
                continue;
            if (rule.StartDate.HasValue && rule.StartDate.Value > now || rule.EndDate.HasValue && rule.EndDate.Value <= now)
                continue;

            var candidate = rule.DiscountType switch
            {
                DiscountType.Percentage => unitPrice * (1 - Math.Clamp(rule.DiscountValue, 0, 100) / 100),
                DiscountType.FixedAmount => unitPrice - Math.Max(0, rule.DiscountValue),
                DiscountType.FixedPrice => rule.DiscountValue,
                _ => unitPrice
            };
            discountedPrice = Math.Min(discountedPrice, Math.Clamp(candidate, 0, unitPrice));
        }
        return decimal.Round(discountedPrice, 2, MidpointRounding.AwayFromZero);
    }
}
