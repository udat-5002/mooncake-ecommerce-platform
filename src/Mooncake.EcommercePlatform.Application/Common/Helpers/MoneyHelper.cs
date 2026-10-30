namespace Mooncake.EcommercePlatform.Application.Common.Helpers;

using Mooncake.EcommercePlatform.Application.Common.Exceptions;

/// <summary>Validates values before writing to the existing decimal(12, 2) money columns.</summary>
public static class MoneyHelper
{
    public const decimal MaximumAmount = 9_999_999_999.99m;
    private const string InvalidAmount = "Monetary values must be within the supported range and have at most two decimal places.";

    /// <summary>Rejects precision loss and out-of-range amounts; optional amounts may be absent.</summary>
    public static void RequireAmount(decimal? value, decimal minimum = 0m, decimal maximum = MaximumAmount)
    {
        if (value is decimal amount && (amount < minimum || amount > maximum || decimal.Round(amount, 2) != amount))
            throw new HttpException(400, InvalidAmount);
    }
}
