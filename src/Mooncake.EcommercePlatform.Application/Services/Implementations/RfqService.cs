namespace Mooncake.EcommercePlatform.Application.Services.Implementations;

using Mooncake.EcommercePlatform.Application.Common.Exceptions;
using Mooncake.EcommercePlatform.Application.Common.Helpers;
using Mooncake.EcommercePlatform.Application.Common.Interfaces;
using Mooncake.EcommercePlatform.Application.DTOs.Rfqs.Requests;
using Mooncake.EcommercePlatform.Application.DTOs.Rfqs.Responses;
using Mooncake.EcommercePlatform.Application.Services.Interfaces;
using Mooncake.EcommercePlatform.Domain.Entities;
using Mooncake.EcommercePlatform.Domain.Enums;

public sealed class RfqService(IRfqRepository repository, IDateTimeProvider clock) : IRfqService
{
    private const int MaximumInvitations = 10;

    public async Task<RfqResponse> CreateAsync(Guid customerUserId, CreateRfqRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var now = clock.UtcNow;
        var rfq = new RequestForQuotation
        {
            Id = Guid.NewGuid(),
            CustomerId = customerUserId,
            RfqNumber = $"RFQ-{now:yyyy}-{Guid.NewGuid():N}"[..19].ToUpperInvariant(),
            Title = request.Title.Trim(),
            Description = request.Description?.Trim(),
            DeliveryAddress = request.DeliveryAddress.Trim(),
            DeliveryDateRequired = request.DeliveryDateRequired,
            BudgetRangeMin = request.BudgetRangeMin,
            BudgetRangeMax = request.BudgetRangeMax,
            BidDeadline = DateTime.SpecifyKind(request.BidDeadline, DateTimeKind.Utc),
            Status = RfqStatus.Draft,
            CreatedBy = customerUserId
        };
        var items = request.Items.Select(item => new RfqItem
        {
            RfqId = rfq.Id,
            ProductName = item.ProductName.Trim(),
            Description = item.Description?.Trim(),
            Quantity = item.Quantity,
            Unit = item.Unit.Trim(),
            TargetPrice = item.TargetPrice,
            Specifications = item.Specifications?.Trim(),
            PackagingRequirements = item.PackagingRequirements?.Trim(),
            NeedsCustomPackaging = item.NeedsCustomPackaging,
            CreatedBy = customerUserId
        }).ToList();
        if (!await repository.CreateRfqAsync(rfq, items, cancellationToken))
            throw new HttpException(409, "The RFQ could not be created. Please retry the request.");
        return await ToResponseAsync(rfq, false, cancellationToken);
    }

    public async Task<RfqResponse> PublishAsync(Guid customerUserId, Guid rfqId, CancellationToken cancellationToken = default)
    {
        var rfq = await GetOwnedRfqAsync(customerUserId, rfqId, cancellationToken);
        if (rfq.Status != RfqStatus.Draft)
            throw new HttpException(409, "Only a draft RFQ can be published.");
        if (rfq.BidDeadline <= clock.UtcNow || rfq.DeliveryDateRequired < DateOnly.FromDateTime(clock.UtcNow))
            throw new HttpException(400, "The bid deadline and required delivery date must be in the future.");
        if (await repository.GetRfqItemsAsync(rfqId, cancellationToken) is not { Count: > 0 })
            throw new HttpException(400, "An RFQ must include at least one requested item.");
        rfq.Status = RfqStatus.Open;
        rfq.UpdatedBy = customerUserId;
        await repository.SaveRfqAsync(rfq, cancellationToken);
        return await ToResponseAsync(rfq, false, cancellationToken);
    }

    public async Task CancelAsync(Guid customerUserId, Guid rfqId, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new HttpException(400, "A cancellation reason is required.");
        var rfq = await GetOwnedRfqAsync(customerUserId, rfqId, cancellationToken);
        if (rfq.Status is RfqStatus.Awarded or RfqStatus.Cancelled)
            throw new HttpException(409, "An awarded or cancelled RFQ cannot be cancelled.");
        if (!await repository.CancelRfqAsync(rfqId, customerUserId, reason.Trim(), cancellationToken))
            throw new HttpException(409, "The RFQ changed before it could be cancelled.");
    }

    public async Task<IReadOnlyList<RfqResponse>> GetMineAsync(Guid customerUserId, CancellationToken cancellationToken = default)
    {
        var rfqs = await repository.GetCustomerRfqsAsync(customerUserId, cancellationToken);
        var responses = new List<RfqResponse>(rfqs.Count);
        foreach (var rfq in rfqs) responses.Add(await ToResponseAsync(rfq, false, cancellationToken));
        return responses;
    }

    public async Task<RfqResponse> GetAsync(Guid userId, bool isSupplier, Guid rfqId, CancellationToken cancellationToken = default)
    {
        var rfq = await repository.GetRfqAsync(rfqId, cancellationToken);
        if (rfq is null || rfq.IsDeleted) throw new HttpException(404, "RFQ was not found.");
        if (rfq.CustomerId != userId)
        {
            var supplierId = isSupplier ? await repository.GetVerifiedSupplierProfileIdAsync(userId, cancellationToken) : null;
            if (!supplierId.HasValue || await repository.GetInvitationAsync(rfqId, supplierId.Value, cancellationToken) is null)
                throw new HttpException(404, "RFQ was not found.");
            if (rfq.Status == RfqStatus.Open && rfq.BidDeadline > clock.UtcNow)
                await MarkViewedAsync(rfqId, supplierId.Value, cancellationToken);
        }
        return await ToResponseAsync(rfq, rfq.CustomerId != userId, cancellationToken);
    }

    public async Task<RfqResponse> InviteAsync(Guid customerUserId, Guid rfqId, InviteSuppliersRequest request, CancellationToken cancellationToken = default)
    {
        var rfq = await GetOwnedRfqAsync(customerUserId, rfqId, cancellationToken);
        EnsureOpen(rfq);
        if (request.SupplierUserIds.Count == 0) throw new HttpException(400, "Select at least one supplier to invite.");
        var existing = await repository.GetInvitationsAsync(rfqId, cancellationToken);
        var userIds = request.SupplierUserIds.Distinct().ToArray();
        if (userIds.Length != request.SupplierUserIds.Count) throw new HttpException(400, "Supplier IDs must be unique.");
        if (existing.Count + userIds.Length > MaximumInvitations) throw new HttpException(409, "An RFQ can invite at most 10 suppliers.");

        var invited = new List<RfqInvitation>(userIds.Length);
        foreach (var supplierUserId in userIds)
        {
            var profileId = await repository.GetVerifiedSupplierProfileIdAsync(supplierUserId, cancellationToken);
            if (!profileId.HasValue || !await repository.IsActiveSupplierAsync(profileId.Value, cancellationToken))
                throw new HttpException(400, "Every invited supplier must be verified and have an active shop.");
            if (existing.Any(invitation => invitation.SupplierId == profileId.Value))
                throw new HttpException(409, "A supplier has already been invited to this RFQ.");
            invited.Add(new RfqInvitation { RfqId = rfqId, SupplierId = profileId.Value, InvitedAt = clock.UtcNow, CreatedBy = customerUserId });
        }
        await repository.AddInvitationsAsync(invited, cancellationToken);
        return await ToResponseAsync(rfq, false, cancellationToken);
    }

    public async Task DeclineAsync(Guid supplierUserId, Guid rfqId, DeclineInvitationRequest request, CancellationToken cancellationToken = default)
    {
        var profileId = await GetVerifiedSupplierIdAsync(supplierUserId, cancellationToken);
        var rfq = await repository.GetRfqAsync(rfqId, cancellationToken);
        if (rfq is null || rfq.IsDeleted) throw new HttpException(404, "RFQ was not found.");
        EnsureOpen(rfq);
        var invitation = await repository.GetInvitationAsync(rfqId, profileId, cancellationToken)
                         ?? throw new HttpException(404, "RFQ invitation was not found.");
        if (invitation.Status is not (RfqInvitationStatus.Invited or RfqInvitationStatus.Viewed))
            throw new HttpException(409, "This RFQ invitation has already been answered.");
        invitation.Status = RfqInvitationStatus.Declined;
        invitation.RespondedAt = clock.UtcNow;
        invitation.DeclineReason = request.Reason.Trim();
        invitation.UpdatedBy = supplierUserId;
        await repository.SaveInvitationAsync(invitation, cancellationToken);
    }

    public async Task<BidResponse> SubmitBidAsync(Guid supplierUserId, Guid rfqId, SubmitBidRequest request, CancellationToken cancellationToken = default)
    {
        var supplierId = await GetVerifiedSupplierIdAsync(supplierUserId, cancellationToken);
        if (!await repository.IsActiveSupplierAsync(supplierId, cancellationToken))
            throw new HttpException(403, "An active supplier shop is required to submit a bid.");
        var rfq = await repository.GetRfqAsync(rfqId, cancellationToken);
        if (rfq is null || rfq.IsDeleted) throw new HttpException(404, "RFQ was not found.");
        EnsureOpen(rfq);
        var invitation = await repository.GetInvitationAsync(rfqId, supplierId, cancellationToken)
                         ?? throw new HttpException(404, "RFQ invitation was not found.");
        if (invitation.Status is RfqInvitationStatus.Declined or RfqInvitationStatus.BidSubmitted)
            throw new HttpException(409, "This RFQ invitation has already been answered.");
        var rfqItems = await repository.GetRfqItemsAsync(rfqId, cancellationToken);
        if (request.Items.Count == 0 || request.Items.Any(item => item.UnitPrice <= 0)
            || request.Items.Count != rfqItems.Count || request.Items.Select(item => item.RfqItemId).Distinct().Count() != request.Items.Count
            || request.Items.Any(item => rfqItems.All(rfqItem => rfqItem.Id != item.RfqItemId)))
            throw new HttpException(400, "A bid must include a price for every RFQ item exactly once.");
        var rfqItemMap = rfqItems.ToDictionary(item => item.Id);
        foreach (var item in request.Items)
            MoneyHelper.RequireAmount(item.UnitPrice, 0.01m);
        var now = clock.UtcNow;
        var bid = new Bid
        {
            Id = Guid.NewGuid(),
            RfqId = rfqId,
            SupplierId = supplierId,
            BidNumber = $"BID-{now:yyyy}-{Guid.NewGuid():N}"[..19].ToUpperInvariant(),
            EstimatedDeliveryDays = request.EstimatedDeliveryDays,
            ValidityDays = request.ValidityDays,
            Notes = request.Notes?.Trim(),
            SubmittedAt = now,
            Status = BidStatus.Submitted,
            CreatedBy = supplierUserId
        };
        var items = request.Items.Select(item => new BidItem
        {
            BidId = bid.Id,
            RfqItemId = item.RfqItemId,
            UnitPrice = item.UnitPrice,
            Quantity = rfqItemMap[item.RfqItemId].Quantity,
            Subtotal = decimal.Round(item.UnitPrice * rfqItemMap[item.RfqItemId].Quantity, 2, MidpointRounding.AwayFromZero),
            Notes = item.Notes?.Trim(),
            CreatedBy = supplierUserId
        }).ToList();
        bid.TotalPrice = items.Sum(item => item.Subtotal);
        MoneyHelper.RequireAmount(bid.TotalPrice);
        if (!await repository.CreateBidAsync(bid, items, cancellationToken))
            throw new HttpException(409, "This supplier has already submitted a bid for this RFQ.");
        return await ToBidResponseAsync(bid, cancellationToken);
    }

    public async Task<IReadOnlyList<BidResponse>> CompareBidsAsync(Guid customerUserId, Guid rfqId, CancellationToken cancellationToken = default)
    {
        _ = await GetOwnedRfqAsync(customerUserId, rfqId, cancellationToken);
        var bids = await repository.GetBidsAsync(rfqId, cancellationToken: cancellationToken);
        var responses = new List<BidResponse>(bids.Count);
        foreach (var bid in bids) responses.Add(await ToBidResponseAsync(bid, cancellationToken));
        return responses;
    }

    public async Task<BidResponse> CancelBidAsync(Guid customerUserId, Guid rfqId, Guid bidId, CancellationToken cancellationToken = default)
    {
        var rfq = await GetOwnedRfqAsync(customerUserId, rfqId, cancellationToken);
        if (rfq.Status is RfqStatus.Awarded or RfqStatus.Cancelled) throw new HttpException(409, "This RFQ no longer accepts bid changes.");
        var bid = await repository.GetBidAsync(bidId, cancellationToken);
        if (bid is null || bid.RfqId != rfqId) throw new HttpException(404, "Bid was not found.");
        if (!await repository.CancelBidAsync(rfqId, bidId, customerUserId, cancellationToken))
            throw new HttpException(409, "Only a submitted bid can be cancelled.");
        bid.Status = BidStatus.Cancelled;
        return await ToBidResponseAsync(bid, cancellationToken);
    }

    public async Task<Contract> AwardBidAsync(Guid customerUserId, Guid rfqId, Guid bidId, AwardBidRequest request, CancellationToken cancellationToken = default)
    {
        var rfq = await GetOwnedRfqAsync(customerUserId, rfqId, cancellationToken);
        if (rfq.Status == RfqStatus.Awarded)
        {
            if (rfq.AwardedBidId != bidId) throw new HttpException(409, "A different bid has already been awarded for this RFQ.");
            return await repository.AwardBidAsync(rfqId, bidId, request.SelectionNote?.Trim(), customerUserId, cancellationToken)
                   ?? throw new HttpException(409, "The awarded contract could not be retrieved.");
        }
        if (rfq.Status is not (RfqStatus.Open or RfqStatus.Closed)) throw new HttpException(409, "This RFQ has already been cancelled.");
        var invitations = await repository.GetInvitationsAsync(rfqId, cancellationToken);
        var allInvitationsAnswered = invitations.Count > 0 && invitations.All(invitation => invitation.Status is RfqInvitationStatus.BidSubmitted or RfqInvitationStatus.Declined);
        if (rfq.BidDeadline > clock.UtcNow && !allInvitationsAnswered)
            throw new HttpException(409, "Bids can be selected after the deadline or after every invited supplier has responded.");
        var selected = await repository.GetBidAsync(bidId, cancellationToken);
        if (selected is null || selected.RfqId != rfqId || selected.Status != BidStatus.Submitted)
            throw new HttpException(404, "A submitted bid for this RFQ was not found.");
        if (selected.SubmittedAt.AddDays(selected.ValidityDays) <= clock.UtcNow)
            throw new HttpException(409, "This bid has expired.");
        return await repository.AwardBidAsync(rfqId, bidId, request.SelectionNote?.Trim(), customerUserId, cancellationToken)
               ?? throw new HttpException(409, "The RFQ or bid changed while it was being awarded. Refresh and try again.");
    }

    private async Task<RfqResponse> ToResponseAsync(RequestForQuotation rfq, bool hideTargetPrice, CancellationToken cancellationToken)
    {
        var items = await repository.GetRfqItemsAsync(rfq.Id, cancellationToken);
        var invitations = await repository.GetInvitationsAsync(rfq.Id, cancellationToken);
        var bids = await repository.GetBidsAsync(rfq.Id, cancellationToken: cancellationToken);
        return new RfqResponse(rfq.Id, rfq.RfqNumber, rfq.Title, rfq.Description, rfq.DeliveryAddress, rfq.DeliveryDateRequired,
            rfq.BudgetRangeMin, rfq.BudgetRangeMax, rfq.BidDeadline, rfq.Status,
            items.Select(item => new RfqItemResponse(item.Id, item.ProductName, item.Description, item.Quantity, item.Unit, hideTargetPrice ? null : item.TargetPrice,
                item.Specifications, item.PackagingRequirements, item.NeedsCustomPackaging)).ToArray(),
            invitations.Count, bids.Count);
    }

    private async Task<BidResponse> ToBidResponseAsync(Bid bid, CancellationToken cancellationToken)
    {
        var rfqItems = await repository.GetRfqItemsAsync(bid.RfqId, cancellationToken);
        var byId = rfqItems.ToDictionary(item => item.Id);
        var items = await repository.GetBidItemsAsync(bid.Id, cancellationToken);
        return new BidResponse(bid.Id, bid.BidNumber, bid.SupplierId, bid.TotalPrice, bid.EstimatedDeliveryDays, bid.ValidityDays,
            bid.SubmittedAt, bid.SubmittedAt.AddDays(bid.ValidityDays) <= clock.UtcNow, bid.Status,
            items.Select(item => new BidItemResponse(item.RfqItemId, byId.GetValueOrDefault(item.RfqItemId)?.ProductName,
                item.Quantity, byId.GetValueOrDefault(item.RfqItemId)?.Unit, item.UnitPrice, item.Subtotal, item.Notes)).ToArray());
    }

    private async Task<RequestForQuotation> GetOwnedRfqAsync(Guid customerUserId, Guid rfqId, CancellationToken cancellationToken)
    {
        var rfq = await repository.GetRfqAsync(rfqId, cancellationToken);
        if (rfq is null || rfq.IsDeleted || rfq.CustomerId != customerUserId) throw new HttpException(404, "RFQ was not found.");
        return rfq;
    }

    private async Task<Guid> GetVerifiedSupplierIdAsync(Guid supplierUserId, CancellationToken cancellationToken) =>
        await repository.GetVerifiedSupplierProfileIdAsync(supplierUserId, cancellationToken)
        ?? throw new HttpException(403, "A verified supplier profile is required.");

    private async Task MarkViewedAsync(Guid rfqId, Guid supplierId, CancellationToken cancellationToken)
    {
        var invitation = await repository.GetInvitationAsync(rfqId, supplierId, cancellationToken);
        if (invitation is { Status: RfqInvitationStatus.Invited })
        {
            invitation.Status = RfqInvitationStatus.Viewed;
            await repository.SaveInvitationAsync(invitation, cancellationToken);
        }
    }

    private void EnsureOpen(RequestForQuotation rfq)
    {
        if (rfq.Status != RfqStatus.Open || rfq.BidDeadline <= clock.UtcNow)
            throw new HttpException(409, "The RFQ is closed and no longer accepts responses.");
    }

    private void ValidateRequest(CreateRfqRequest request)
    {
        MoneyHelper.RequireAmount(request.BudgetRangeMin);
        MoneyHelper.RequireAmount(request.BudgetRangeMax);
        foreach (var item in request.Items)
            MoneyHelper.RequireAmount(item.TargetPrice);
        var today = DateOnly.FromDateTime(clock.UtcNow);
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.DeliveryAddress)
            || request.Items.Count == 0 || request.Items.Any(item => string.IsNullOrWhiteSpace(item.ProductName)
                || string.IsNullOrWhiteSpace(item.Unit) || item.Quantity <= 0))
            throw new HttpException(400, "An RFQ requires a title, delivery address, and at least one valid item.");
        if (request.BudgetRangeMin < 0 || request.BudgetRangeMax < 0
            || request.BudgetRangeMin.HasValue && request.BudgetRangeMax.HasValue && request.BudgetRangeMin > request.BudgetRangeMax)
            throw new HttpException(400, "The RFQ budget range is invalid.");
        if (request.BidDeadline.Kind == DateTimeKind.Unspecified || request.BidDeadline <= clock.UtcNow
            || request.DeliveryDateRequired < today || request.BidDeadline.Date > request.DeliveryDateRequired.ToDateTime(TimeOnly.MinValue))
            throw new HttpException(400, "The bid deadline must be in the future and before the required delivery date.");
    }
}
