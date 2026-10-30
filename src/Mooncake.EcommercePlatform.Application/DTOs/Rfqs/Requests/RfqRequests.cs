namespace Mooncake.EcommercePlatform.Application.DTOs.Rfqs.Requests;

using System.ComponentModel.DataAnnotations;

public sealed record CreateRfqRequest
{
    [Required, MaxLength(200)] public string Title { get; init; } = string.Empty;
    [MaxLength(5000)] public string? Description { get; init; }
    [Required, MaxLength(1000)] public string DeliveryAddress { get; init; } = string.Empty;
    [Required] public DateOnly DeliveryDateRequired { get; init; }
    public decimal? BudgetRangeMin { get; init; }
    public decimal? BudgetRangeMax { get; init; }
    [Required] public DateTime BidDeadline { get; init; }
    [Required, MinLength(1), MaxLength(100)] public List<CreateRfqItemRequest> Items { get; init; } = [];
}

public sealed record CreateRfqItemRequest
{
    [Required, MaxLength(200)] public string ProductName { get; init; } = string.Empty;
    [MaxLength(2000)] public string? Description { get; init; }
    [Range(1, int.MaxValue)] public int Quantity { get; init; }
    [Required, MaxLength(50)] public string Unit { get; init; } = string.Empty;
    [Range(0, double.MaxValue)] public decimal? TargetPrice { get; init; }
    [MaxLength(4000)] public string? Specifications { get; init; }
    [MaxLength(1000)] public string? PackagingRequirements { get; init; }
    public bool NeedsCustomPackaging { get; init; }
}

public sealed record InviteSuppliersRequest
{
    [Required, MinLength(1), MaxLength(10)] public List<Guid> SupplierUserIds { get; init; } = [];
}

public sealed record DeclineInvitationRequest
{
    [Required, MinLength(3), MaxLength(1000)] public string Reason { get; init; } = string.Empty;
}

public sealed record SubmitBidRequest
{
    [Required, MinLength(1), MaxLength(100)] public List<SubmitBidItemRequest> Items { get; init; } = [];
    [Range(1, 365)] public int EstimatedDeliveryDays { get; init; }
    [Range(1, 365)] public int ValidityDays { get; init; } = 30;
    [MaxLength(4000)] public string? Notes { get; init; }
}

public sealed record SubmitBidItemRequest
{
    [Required] public Guid RfqItemId { get; init; }
    [Range(typeof(decimal), "0.01", "9999999999.99")] public decimal UnitPrice { get; init; }
    [MaxLength(1000)] public string? Notes { get; init; }
}

public sealed record AwardBidRequest
{
    [MaxLength(2000)] public string? SelectionNote { get; init; }
}
