namespace Mooncake.EcommercePlatform.Application.DTOs.Admin;

using System.ComponentModel.DataAnnotations;

using Mooncake.EcommercePlatform.Domain.Enums;
using Mooncake.EcommercePlatform.Application.DTOs.Catalog.Responses;

public sealed record AdminDashboardResponse(int TotalUsers, int ActiveUsers, int PendingSuppliers, int ActiveShops,
    int TotalOrders, decimal PaidGmv, int PendingPayments, int DisputedContracts,
    IReadOnlyList<AdminTopSupplierResponse> TopSuppliers, IReadOnlyList<AdminTopProductResponse> TopProducts);
public sealed record AdminTopSupplierResponse(Guid SupplierId, string? CompanyName, decimal SalesAmount);
public sealed record AdminTopProductResponse(Guid ProductId, string ProductName, int UnitsSold, decimal SalesAmount);
public sealed record SystemConfigResponse(string Key, string ValueJson, string? Description, DateTime UpdatedAtUtc);

public sealed record ContractDepositRuleResponse(Guid Id, string Name, decimal DepositPercentage, bool IsMandatory, decimal? MinContractValue, int PaymentDeadlineHours);

public sealed record SaveContractDepositRuleRequest
{
    [Required, MaxLength(255)] public string Name { get; init; } = string.Empty;
    [Range(typeof(decimal), "0.01", "100")] public decimal DepositPercentage { get; init; }
    public bool IsMandatory { get; init; } = true;
    [Range(typeof(decimal), "0", "9999999999.99")] public decimal? MinContractValue { get; init; }
    [Range(1, 720)] public int PaymentDeadlineHours { get; init; } = 48;
}
public sealed record AuditLogResponse(Guid Id, Guid? ActorUserId, string Action, string EntityName, string? EntityId,
    string? MetadataJson, string? IpAddress, DateTime CreatedAtUtc);
public sealed record AdminShopResponse(ShopResponse Shop, int ProductCount);
public sealed record AdminUserUpdateRequest(bool? IsActive, UserRole? Role);
