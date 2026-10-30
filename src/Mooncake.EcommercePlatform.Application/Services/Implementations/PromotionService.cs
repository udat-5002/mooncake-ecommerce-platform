namespace Mooncake.EcommercePlatform.Application.Services.Implementations;

using Mooncake.EcommercePlatform.Application.Common.Exceptions;
using Mooncake.EcommercePlatform.Application.Common.Helpers;
using Mooncake.EcommercePlatform.Application.Common.Interfaces;
using Mooncake.EcommercePlatform.Application.DTOs.Promotions.Requests;
using Mooncake.EcommercePlatform.Application.DTOs.Promotions.Responses;
using Mooncake.EcommercePlatform.Application.Services.Interfaces;
using Mooncake.EcommercePlatform.Domain.Entities;
using Mooncake.EcommercePlatform.Domain.Enums;

/// <summary>Implements supplier-owned quantity-tier promotions.</summary>
public sealed class PromotionService(IPromotionRepository repository, IDateTimeProvider dateTimeProvider) : IPromotionService
{
    public async Task<IReadOnlyList<PromotionResponse>> GetMineAsync(Guid supplierUserId, CancellationToken cancellationToken = default)
    {
        var shop = await GetVerifiedShopAsync(supplierUserId, cancellationToken);
        var promotions = await repository.GetForShopAsync(shop.Id, cancellationToken);
        return promotions.Select(PromotionResponseHelper.ToResponse).ToList();
    }

    public async Task<PromotionResponse> CreateAsync(Guid supplierUserId, SavePromotionRequest request, CancellationToken cancellationToken = default)
    {
        var shop = await GetVerifiedShopAsync(supplierUserId, cancellationToken);
        await ValidateAsync(shop.Id, request, null, cancellationToken);
        var promotion = BuildPromotion(shop.Id, request, supplierUserId);
        await repository.SaveAsync(promotion, cancellationToken);
        return PromotionResponseHelper.ToResponse(promotion);
    }

    public async Task<PromotionResponse> UpdateAsync(Guid supplierUserId, Guid promotionId, SavePromotionRequest request, CancellationToken cancellationToken = default)
    {
        var shop = await GetVerifiedShopAsync(supplierUserId, cancellationToken);
        var promotion = await repository.GetAsync(promotionId, cancellationToken)
                        ?? throw new HttpException(404, "Promotion was not found.");
        if (promotion.ShopId != shop.Id)
            throw new HttpException(404, "Promotion was not found.");
        await ValidateAsync(shop.Id, request, promotionId, cancellationToken);

        promotion.Name = request.Name.Trim();
        promotion.Description = request.Description?.Trim();
        promotion.ProductId = request.ProductId;
        promotion.MinQty = request.MinQty;
        promotion.MaxQty = request.MaxQty;
        promotion.DiscountType = request.DiscountType;
        promotion.DiscountValue = request.DiscountValue;
        promotion.StartDate = request.StartAtUtc?.UtcDateTime;
        promotion.EndDate = request.EndAtUtc?.UtcDateTime;
        promotion.IsActive = true;
        promotion.UpdatedBy = supplierUserId;
        promotion.UpdatedAt = dateTimeProvider.UtcNow;
        await repository.SaveAsync(promotion, cancellationToken);
        return PromotionResponseHelper.ToResponse(promotion);
    }

    public async Task DeactivateAsync(Guid supplierUserId, Guid promotionId, CancellationToken cancellationToken = default)
    {
        var shop = await GetVerifiedShopAsync(supplierUserId, cancellationToken);
        var promotion = await repository.GetAsync(promotionId, cancellationToken)
                        ?? throw new HttpException(404, "Promotion was not found.");
        if (promotion.ShopId != shop.Id)
            throw new HttpException(404, "Promotion was not found.");
        promotion.IsActive = false;
        promotion.UpdatedBy = supplierUserId;
        promotion.UpdatedAt = dateTimeProvider.UtcNow;
        await repository.SaveAsync(promotion, cancellationToken);
    }

    private async Task<Shop> GetVerifiedShopAsync(Guid supplierUserId, CancellationToken cancellationToken) =>
        await repository.GetVerifiedShopAsync(supplierUserId, cancellationToken)
        ?? throw new HttpException(403, "A verified supplier with an active shop is required.");

    private async Task ValidateAsync(Guid shopId, SavePromotionRequest request, Guid? exceptPromotionId, CancellationToken cancellationToken)
    {
        MoneyHelper.RequireAmount(request.DiscountValue, 0.01m);
        if (!Enum.IsDefined(request.DiscountType))
            throw new HttpException(400, "The selected discount type is invalid.");
        if (request.MaxQty.HasValue && request.MaxQty.Value < request.MinQty)
            throw new HttpException(400, "Maximum quantity must be greater than or equal to minimum quantity.");
        if (request.StartAtUtc.HasValue && request.EndAtUtc.HasValue && request.EndAtUtc <= request.StartAtUtc)
            throw new HttpException(400, "Promotion end time must be later than its start time.");
        if (request.EndAtUtc.HasValue && request.EndAtUtc <= dateTimeProvider.UtcNow)
            throw new HttpException(400, "Promotion end time must be in the future.");
        if (request.DiscountType == DiscountType.Percentage && request.DiscountValue > 100)
            throw new HttpException(400, "Percentage discounts cannot exceed 100 percent.");
        if (request.DiscountType == DiscountType.FixedPrice && !request.ProductId.HasValue)
            throw new HttpException(400, "Fixed-price discounts must target a specific product.");
        if (request.ProductId.HasValue && !await repository.ProductBelongsToShopAsync(request.ProductId.Value, shopId, cancellationToken))
            throw new HttpException(400, "The selected product does not belong to this shop.");
        if (await repository.NameExistsAsync(shopId, request.Name.Trim(), exceptPromotionId, cancellationToken))
            throw new HttpException(409, "A promotion with this name already exists in the shop.");
    }

    private PromotionRule BuildPromotion(Guid shopId, SavePromotionRequest request, Guid supplierUserId) => new()
    {
        ShopId = shopId,
        ProductId = request.ProductId,
        Name = request.Name.Trim(),
        Description = request.Description?.Trim(),
        MinQty = request.MinQty,
        MaxQty = request.MaxQty,
        DiscountType = request.DiscountType,
        DiscountValue = request.DiscountValue,
        StartDate = request.StartAtUtc?.UtcDateTime,
        EndDate = request.EndAtUtc?.UtcDateTime,
        IsActive = true,
        CreatedBy = supplierUserId,
        CreatedAt = dateTimeProvider.UtcNow,
        UpdatedAt = dateTimeProvider.UtcNow
    };
}
