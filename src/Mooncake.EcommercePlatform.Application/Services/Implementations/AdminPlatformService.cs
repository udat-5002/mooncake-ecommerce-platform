namespace Mooncake.EcommercePlatform.Application.Services.Implementations;

using System.Text.Json;
using Mooncake.EcommercePlatform.Application.Common.Exceptions;
using Mooncake.EcommercePlatform.Application.Common.Helpers;
using Mooncake.EcommercePlatform.Application.Common.Interfaces;
using Mooncake.EcommercePlatform.Application.DTOs.Admin;
using Mooncake.EcommercePlatform.Application.Services.Interfaces;
using Mooncake.EcommercePlatform.Domain.Entities;
using Mooncake.EcommercePlatform.Domain.Enums;

public sealed class AdminPlatformService(IAdminPlatformRepository repository, IUserRepository userRepository, IDateTimeProvider dateTimeProvider, ISupplierRepository verification) : IAdminPlatformService
{
    private const string ConfigAction = "SystemConfigUpdated";
    private const string UserAction = "UserUpdatedByAdmin";

    public async Task<AdminDashboardResponse> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var metrics = await repository.GetMetricsAsync(cancellationToken);
        return new(metrics.TotalUsers, metrics.ActiveUsers, metrics.PendingSuppliers, metrics.ActiveShops,
            metrics.TotalOrders, metrics.PaidGmv, metrics.PendingPayments, metrics.DisputedContracts,
            metrics.TopSuppliers.Select(item => new AdminTopSupplierResponse(item.SupplierId, item.CompanyName, item.SalesAmount)).ToList(),
            metrics.TopProducts.Select(item => new AdminTopProductResponse(item.ProductId, item.ProductName, item.UnitsSold, item.SalesAmount)).ToList());
    }

    public async Task<IReadOnlyList<SystemConfigResponse>> GetSystemConfigsAsync(CancellationToken cancellationToken = default) =>
        (await repository.GetSystemConfigsAsync(cancellationToken))
        .Select(config => new SystemConfigResponse(config.Key, config.ValueJson, config.Description, config.UpdatedAtUtc)).ToList();

    public async Task<SystemConfigResponse> SetSystemConfigAsync(string key, string valueJson, string? description, Guid actorId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 120) throw new HttpException(400, "Configuration key must contain 1 to 120 characters.");
        try { using var _ = JsonDocument.Parse(valueJson); }
        catch (JsonException) { throw new HttpException(400, "Configuration value must be valid JSON."); }

        var config = await repository.GetSystemConfigAsync(key.Trim(), cancellationToken);
        var now = dateTimeProvider.UtcNow;
        if (config is null) config = new SystemConfig { Key = key.Trim(), CreatedAt = now };
        config.ValueJson = valueJson;
        config.Description = description?.Trim();
        config.UpdatedAt = now;
        await repository.SaveSystemConfigAsync(config, cancellationToken);
        await repository.SaveAuditAsync(new AuditLog
        {
            ActorUserId = actorId, Action = ConfigAction, EntityName = nameof(SystemConfig), EntityId = config.Id.ToString(),
            MetadataJson = JsonSerializer.Serialize(new { config.Key, config.ValueJson }), IpAddress = ipAddress, CreatedAt = now
        }, cancellationToken);
        return new(config.Key, config.ValueJson, config.Description, config.UpdatedAtUtc);
    }

    public async Task<IReadOnlyList<AuditLogResponse>> GetAuditLogsAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize is < 1 or > 200) throw new HttpException(400, "Page must be positive and page size must be between 1 and 200.");
        return (await repository.GetAuditLogsAsync(page, pageSize, cancellationToken))
            .Select(log => new AuditLogResponse(log.Id, log.ActorUserId, log.Action, log.EntityName, log.EntityId,
                log.MetadataJson, log.IpAddress, log.CreatedAtUtc)).ToList();
    }

    public async Task UpdateUserAsync(Guid userId, AdminUserUpdateRequest request, Guid actorId, string? ipAddress, CancellationToken cancellationToken = default)
    {

        await verification.InTransactionAsync([actorId, userId], async token =>
        {
            Mooncake.EcommercePlatform.Application.Common.Helpers.SupplierVerificationHelper.RequireAdmin(
                await verification.GetUserAsync(actorId, token), userId);
    if (request.IsActive is null && request.Role is null) throw new HttpException(400, "At least one user field must be supplied.");
            if (request.Role is UserRole suppliedRole && !Enum.IsDefined(suppliedRole)) throw new HttpException(400, "Role value is invalid.");
            if (request.Role == UserRole.Admin) throw new HttpException(400, "Admin role cannot be assigned through this endpoint.");
            var user = await repository.GetUserForUpdateAsync(userId, cancellationToken) ?? throw new HttpException(404, "User was not found.");
            if (user.Role == UserRole.Admin) throw new HttpException(409, "Administrative accounts cannot be changed through this endpoint.");
            if (request.Role == UserRole.Supplier && !await repository.IsSupplierVerifiedAsync(userId, cancellationToken))
                throw new HttpException(409, "Only verified supplier accounts can be assigned the Supplier role.");
            var previous = new { user.IsActive, user.Role };
            if (request.IsActive is bool active) user.IsActive = active;
            if (request.Role is UserRole role) user.Role = role;
            user.UpdatedBy = actorId;
            user.UpdatedAt = dateTimeProvider.UtcNow;
            var audit = new AuditLog
            {
                ActorUserId = actorId, Action = UserAction, EntityName = nameof(User), EntityId = userId.ToString(),
                MetadataJson = JsonSerializer.Serialize(new { Before = previous, After = new { user.IsActive, user.Role } }),
                IpAddress = ipAddress, CreatedAt = dateTimeProvider.UtcNow
            };
            verification.AddAudit(audit);
            if (request.IsActive is false)
                await userRepository.RevokeAllRefreshSessionsAsync(userId, dateTimeProvider.UtcNow, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<ContractDepositRuleResponse>> GetContractDepositRulesAsync(CancellationToken cancellationToken = default) =>
        (await repository.GetContractDepositRulesAsync(cancellationToken))
        .Select(rule => new ContractDepositRuleResponse(rule.Id, rule.Name, rule.DepositPercentage, rule.IsMandatory,
            rule.MinContractValue, rule.PaymentDeadlineHours)).ToArray();

    public async Task<ContractDepositRuleResponse> SaveContractDepositRuleAsync(Guid? ruleId, SaveContractDepositRuleRequest request,
        Guid actorId, string? ipAddress, CancellationToken cancellationToken = default)
    {
        MoneyHelper.RequireAmount(request.DepositPercentage, 0.01m, 100m);
        MoneyHelper.RequireAmount(request.MinContractValue);
        var rule = ruleId.HasValue
            ? await repository.GetContractDepositRuleAsync(ruleId.Value, cancellationToken) ?? throw new HttpException(404, "Deposit rule was not found.")
            : new ContractDepositRule { Id = Guid.NewGuid(), CreatedAt = dateTimeProvider.UtcNow, CreatedBy = actorId };
        var oldValue = new { rule.Name, rule.DepositPercentage, rule.IsMandatory, rule.MinContractValue, rule.PaymentDeadlineHours };
        rule.Name = request.Name.Trim();
        rule.Description = "Applies to all contracts whose value meets the configured threshold.";
        rule.DepositPercentage = request.DepositPercentage;
        rule.IsMandatory = request.IsMandatory;
        rule.AppliesToAll = true;
        rule.MinContractValue = request.MinContractValue;
        rule.PaymentDeadlineHours = request.PaymentDeadlineHours;
        rule.UpdatedAt = dateTimeProvider.UtcNow;
        rule.UpdatedBy = actorId;
        await repository.SaveContractDepositRuleAndAuditAsync(rule, new AuditLog
        {
            ActorUserId = actorId, Action = "ContractDepositRuleUpdated", EntityName = nameof(ContractDepositRule), EntityId = rule.Id.ToString(),
            MetadataJson = JsonSerializer.Serialize(new { before = oldValue, after = new { rule.Name, rule.DepositPercentage, rule.IsMandatory, rule.MinContractValue, rule.PaymentDeadlineHours } }),
            IpAddress = ipAddress, CreatedAt = dateTimeProvider.UtcNow
        }, cancellationToken);
        return new ContractDepositRuleResponse(rule.Id, rule.Name, rule.DepositPercentage, rule.IsMandatory, rule.MinContractValue, rule.PaymentDeadlineHours);
    }
}
