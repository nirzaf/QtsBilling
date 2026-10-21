using System.Globalization;
using QtsBilling.Contracts.Pricing;

namespace QtsBilling.Ui.Pages.Pricing;

/// <summary>
/// The value rules the pricing screens apply, kept out of the markup so they can be tested against the exact
/// numbers the server accepts (specification task 16, recipe steps 2 and 3). None of this is a second authority:
/// the server still refuses what is out of range, and these functions exist so the screen sends the same unit the
/// column stores rather than a percentage that some other path would divide.
/// </summary>
public static class PricingRules
{
    /// <summary>A tax rate carries at most six decimal places, so a percent input divides without silent rounding.</summary>
    public const int TaxRateDecimals = 6;

    /// <summary>A discount value carries at most four decimal places, in percent units or as an amount.</summary>
    public const int DiscountDecimals = 4;

    /// <summary>15 % becomes the exact fraction 0.15 the server stores.</summary>
    public static decimal PercentToRate(decimal percent)
        => decimal.Round(percent / 100m, TaxRateDecimals, MidpointRounding.AwayFromZero);

    /// <summary>0.15 reads back as 15 %, and 0.025 as 2.5 %.</summary>
    public static decimal RateToPercent(decimal rate)
        => decimal.Round(rate * 100m, TaxRateDecimals, MidpointRounding.AwayFromZero);

    public static string PercentText(decimal rate)
        => RateToPercent(rate).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    /// <summary>The stored form, shown next to the percent so the operator can see what the column holds.</summary>
    public static string StoredText(decimal rate)
        => rate.ToString("0.############", CultureInfo.InvariantCulture);

    /// <summary>True when a percentage entered in percent units is still inside what the server will accept.</summary>
    public static bool PercentIsInRange(decimal percent) => percent >= 0m && percent <= 100m;

    /// <summary>A discount's value, phrased in the unit its kind defines.</summary>
    public static string ValueText(DiscountDto discount)
        => discount.Kind switch
        {
            DiscountKind.Percentage => $"{discount.Value.ToString("0.####", CultureInfo.InvariantCulture)} %",
            _ => discount.Value.ToString("N", CultureInfo.InvariantCulture),
        };

    /// <summary>
    /// The effective window as the operator reads it. The instants are stored in UTC and shown in the business's
    /// own zone, because "the offer ends at midnight" means the counter's midnight, not the server's.
    /// </summary>
    public static string WindowText(DiscountDto discount, TimeZoneInfo businessZone)
    {
        if (discount.StartsAtUtc is null && discount.EndsAtUtc is null)
        {
            return "Always in effect";
        }

        var start = discount.StartsAtUtc is { } startsAt
            ? $"from {ConvertTime(startsAt, businessZone)}"
            : "from now on";
        var end = discount.EndsAtUtc is { } endsAt
            ? $"until {ConvertTime(endsAt, businessZone)}"
            : "with no end date";

        return $"{start} {end}";
    }

    /// <summary>
    /// Whether the definition is usable right now: active, and inside its window if it has one. An expired but
    /// still-active definition is shown as expired rather than as a live one.
    /// </summary>
    public static bool AppliesNow(DiscountDto discount, DateTimeOffset nowUtc)
        => discount.IsActive
            && (discount.StartsAtUtc is null || discount.StartsAtUtc <= nowUtc)
            && (discount.EndsAtUtc is null || discount.EndsAtUtc > nowUtc);

    public static string StatusText(DiscountDto discount, DateTimeOffset nowUtc)
    {
        if (!discount.IsActive)
        {
            return "Taken out of service";
        }

        if (discount.StartsAtUtc is { } start && start > nowUtc)
        {
            return "Not started yet";
        }

        if (discount.EndsAtUtc is { } end && end <= nowUtc)
        {
            return "Expired - it still exists for the documents that used it";
        }

        return "In effect now";
    }

    private static string ConvertTime(DateTimeOffset instant, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTime(instant, zone).ToString("g", CultureInfo.InvariantCulture);

    /// <summary>
    /// A date the operator chose in the business's own zone becomes the instant the server stores. The window
    /// starts at midnight there and ends at the stroke of the following midnight, so "ends on 31 December"
    /// includes the whole of the 31st at the counter rather than cutting off at the server's midnight.
    /// </summary>
    public static DateTimeOffset DayStartUtc(DateOnly day, TimeZoneInfo zone)
    {
        var midnight = new DateTime(day.Year, day.Month, day.Day, 0, 0, 0, DateTimeKind.Unspecified);

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(midnight, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
    }

    /// <summary>The first instant after the chosen day, in UTC - the exclusive end of the window.</summary>
    public static DateTimeOffset DayAfterUtc(DateOnly day, TimeZoneInfo zone)
        => DayStartUtc(day.AddDays(1), zone);
}
