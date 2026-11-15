using System;
using System.Reflection;

namespace eQuantic.Core.Data.Modeling;

/// <summary>
///     What a <see cref="DateStorageAttribute" /> declaration means, the same on every provider: which members may
///     carry it, the UTC value written for a member value, and the member value read back from what the store holds.
///     Providers call these so that a date declared on one store is written, rounded and read back the way it is on
///     every other.
/// </summary>
public static class DateStorageRules
{
    /// <summary>The largest precision any store keeps: microseconds.</summary>
    public const int MaxPrecision = 6;

    /// <summary>The declaration on a member, or <c>null</c>.</summary>
    /// <param name="member">The member.</param>
    public static DateStorageAttribute? For(MemberInfo member) =>
        member.GetCustomAttribute<DateStorageAttribute>(inherit: true);

    /// <summary>Whether a member of <paramref name="type" /> is a date a declaration can apply to.</summary>
    /// <param name="type">The member's type.</param>
    public static bool IsDate(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset);
    }

    /// <summary>
    ///     Throws when a declaration cannot apply: a member that is not a date, or a precision outside 0 to
    ///     <paramref name="maxPrecision" /> (the store's own limit, never above <see cref="MaxPrecision" />), naming
    ///     the member and the store.
    /// </summary>
    /// <param name="memberType">The member's type.</param>
    /// <param name="precision">The declared precision (<c>-1</c> for the store's own).</param>
    /// <param name="member">The member, for the message (<c>Entity.Member</c>).</param>
    /// <param name="store">The store, for the message.</param>
    /// <param name="maxPrecision">The most fractional digits the store keeps.</param>
    public static void Validate(Type memberType, int precision, string member, string store, int maxPrecision = MaxPrecision)
    {
        if (!IsDate(memberType))
        {
            throw new InvalidOperationException(
                $"'{member}' declares [DateStorage], and only a DateTime or DateTimeOffset member is a date; " +
                $"'{(Nullable.GetUnderlyingType(memberType) ?? memberType).Name}' is not.");
        }

        if (precision < -1 || precision > maxPrecision)
        {
            throw new InvalidOperationException(maxPrecision < MaxPrecision && precision > maxPrecision && precision <= MaxPrecision
                ? $"'{member}' declares a precision of {precision}, and {store} keeps dates to {maxPrecision} fractional " +
                  $"digits of a second; declare {maxPrecision} or fewer."
                : $"'{member}' declares a precision of {precision}; a date keeps 0 to {MaxPrecision} fractional digits " +
                  "of a second (or -1 for the store's own).");
        }
    }

    /// <summary>
    ///     The UTC value written for a member value: a <c>Local</c> <c>DateTime</c> converted by its zone, an
    ///     <c>Unspecified</c> one taken as UTC, a <c>DateTimeOffset</c> converted by its offset; rounded to
    ///     <paramref name="precision" /> half away from zero. <c>null</c> stays <c>null</c>.
    /// </summary>
    /// <param name="value">A <c>DateTime</c> or <c>DateTimeOffset</c>, or <c>null</c>.</param>
    /// <param name="precision">The declared precision (<c>-1</c> leaves the value as it is).</param>
    public static DateTime? ToUtc(object? value, int precision)
    {
        DateTime? utc = value switch
        {
            null => null,
            DateTime { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
            DateTime date => DateTime.SpecifyKind(date, DateTimeKind.Utc),
            DateTimeOffset moment => moment.UtcDateTime,
            _ => throw new ArgumentException(
                $"A date declared with [DateStorage] holds a DateTime or DateTimeOffset, not a '{value.GetType().Name}'.",
                nameof(value)),
        };

        return utc is { } instant ? Round(instant, precision) : null;
    }

    /// <summary>
    ///     The member value read back from what the store holds: a <c>DateTime</c> in UTC, or a <c>DateTimeOffset</c>
    ///     with offset zero. A stored <c>DateTime</c> without a kind is the UTC wall time the store kept.
    /// </summary>
    /// <param name="stored">The stored value: a <c>DateTime</c> or <c>DateTimeOffset</c>, or <c>null</c>.</param>
    /// <param name="memberType">The member's type.</param>
    public static object? FromStored(object? stored, Type memberType)
    {
        DateTime? utc = stored switch
        {
            null or DBNull => null,
            DateTime { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
            DateTime date => DateTime.SpecifyKind(date, DateTimeKind.Utc),
            DateTimeOffset moment => moment.UtcDateTime,
            _ => throw new InvalidOperationException(
                $"The store gave back a '{stored.GetType().Name}' for a date declared with [DateStorage]."),
        };

        if (utc is not { } value)
        {
            return null;
        }

        // Boxed on each branch: as one conditional, C# would type both as DateTimeOffset (DateTime converts to it).
        var underlying = Nullable.GetUnderlyingType(memberType) ?? memberType;
        return underlying == typeof(DateTimeOffset) ? (object)new DateTimeOffset(value, TimeSpan.Zero) : value;
    }

    /// <summary>Rounds a date to <paramref name="precision" /> fractional digits of a second, half away from zero.</summary>
    /// <param name="value">The date.</param>
    /// <param name="precision">The fractional digits kept (<c>-1</c> leaves the value as it is).</param>
    public static DateTime Round(DateTime value, int precision)
    {
        if (precision is < 0 or >= 7)
        {
            return value;
        }

        // A tick is a ten-millionth of a second, so precision p keeps a multiple of 10^(7 - p) ticks.
        long unit = 1;
        for (var digit = precision; digit < 7; digit++)
        {
            unit *= 10;
        }

        var remainder = value.Ticks % unit;
        var floor = value.Ticks - remainder;
        var ticks = remainder * 2 >= unit ? floor + unit : floor;
        return new DateTime(Math.Min(ticks, DateTime.MaxValue.Ticks), value.Kind);
    }
}
