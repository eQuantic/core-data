using System;
using System.Globalization;

namespace eQuantic.Core.Data.Evolution;

/// <summary>
///     Renders a declared value as the C# literal that will appear in a generated file. It covers the types a
///     default is actually written as; anything else is refused by name rather than rendered into something that
///     compiles but means something different.
/// </summary>
public static class CSharpLiteral
{
    /// <summary>Renders the value, or <c>null</c> when there is none.</summary>
    /// <param name="value">The value.</param>
    /// <exception cref="NotSupportedException">The type has no faithful literal form.</exception>
    public static string? Render(object? value) => value switch
    {
        null => null,
        string text => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        bool flag => flag ? "true" : "false",
        char character => "'" + character.ToString().Replace("\\", "\\\\").Replace("'", "\\'") + "'",
        byte or sbyte or short or ushort or int => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        uint number => number.ToString(CultureInfo.InvariantCulture) + "u",
        long number => number.ToString(CultureInfo.InvariantCulture) + "L",
        ulong number => number.ToString(CultureInfo.InvariantCulture) + "UL",
        float number => number.ToString("R", CultureInfo.InvariantCulture) + "f",
        double number => number.ToString("R", CultureInfo.InvariantCulture) + "d",
        decimal number => number.ToString(CultureInfo.InvariantCulture) + "m",
        Guid id => $"new Guid(\"{id}\")",
        DateTime moment => $"new DateTime({moment.Ticks}L, DateTimeKind.{moment.Kind})",
        DateTimeOffset moment => $"new DateTimeOffset({moment.Ticks}L, TimeSpan.FromTicks({moment.Offset.Ticks}L))",
        TimeSpan span => $"TimeSpan.FromTicks({span.Ticks}L)",
        Enum member => $"{member.GetType().FullName}.{member}",
        _ => throw new NotSupportedException(
            $"A default of type '{value.GetType().Name}' has no literal form the tooling can write. Declare a " +
            "value of a primitive type, a Guid, a date or an enum, or write the change by hand."),
    };
}
