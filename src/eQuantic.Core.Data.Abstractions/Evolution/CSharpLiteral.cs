using System;
using System.Globalization;
using System.Text;

namespace eQuantic.Core.Data.Evolution;

/// <summary>
///     Renders a declared value as the C# literal that will appear in a generated file. It covers the types a
///     default is actually written as; anything else is refused by name rather than rendered into something that
///     compiles but means something different. Text compiles and reads back as the same text whatever it holds:
///     quotes, backslashes, line breaks (a SQL default written over several lines), tabs, any other control character,
///     the characters C# reads as the end of a line (U+0085, U+2028, U+2029) and surrogates are escaped. A
///     <c>float</c> or a <c>double</c> that is not a finite number is written as the constant that names it
///     (<c>double.NaN</c>, <c>float.PositiveInfinity</c>). An enum value that is one defined member is written as that
///     member, its type named as C# names it (<c>Shop.Order.Status.Active</c> for a nested enum), and any other value
///     (a combination of flags, a number no member has) as a cast of its underlying number
///     (<c>(Shop.Order.Status)42</c>, <c>(Shop.Order.Status)(-1)</c>).
/// </summary>
public static class CSharpLiteral
{
    /// <summary>Renders the value, or <c>null</c> when there is none.</summary>
    /// <param name="value">The value.</param>
    /// <exception cref="NotSupportedException">The type has no faithful literal form.</exception>
    public static string? Render(object? value) => value switch
    {
        null => null,
        string text => Quoted(text, '"'),
        bool flag => flag ? "true" : "false",
        char character => Quoted(character.ToString(), '\''),
        byte or sbyte or short or ushort or int => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        uint number => number.ToString(CultureInfo.InvariantCulture) + "u",
        long number => number.ToString(CultureInfo.InvariantCulture) + "L",
        ulong number => number.ToString(CultureInfo.InvariantCulture) + "UL",
        float number => NonFinite("float", number) ?? number.ToString("R", CultureInfo.InvariantCulture) + "f",
        double number => NonFinite("double", number) ?? number.ToString("R", CultureInfo.InvariantCulture) + "d",
        decimal number => number.ToString(CultureInfo.InvariantCulture) + "m",
        Guid id => $"new Guid(\"{id}\")",
        DateTime moment => $"new DateTime({moment.Ticks}L, DateTimeKind.{moment.Kind})",
        DateTimeOffset moment => $"new DateTimeOffset({moment.Ticks}L, TimeSpan.FromTicks({moment.Offset.Ticks}L))",
        TimeSpan span => $"TimeSpan.FromTicks({span.Ticks}L)",
        Enum member => EnumValue(member),
        _ => throw new NotSupportedException(
            $"A default of type '{value.GetType().Name}' has no literal form the tooling can write. Declare a " +
            "value of a primitive type, a Guid, a date or an enum, or write the change by hand."),
    };

    /// <summary>The constant naming a number that is not finite, or <c>null</c> for a finite one.</summary>
    /// <param name="keyword">The type's keyword (<c>float</c> or <c>double</c>).</param>
    /// <param name="number">The number.</param>
    private static string? NonFinite(string keyword, double number) =>
        double.IsNaN(number) ? keyword + ".NaN"
        : double.IsPositiveInfinity(number) ? keyword + ".PositiveInfinity"
        : double.IsNegativeInfinity(number) ? keyword + ".NegativeInfinity"
        : null;

    /// <summary>
    ///     An enum value: the member, when it is exactly one defined member, or else a cast of its underlying number,
    ///     which is what a combination of flags or a number no member has can only be written as.
    /// </summary>
    private static string EnumValue(Enum member)
    {
        var type = member.GetType();
        var name = type.FullName;
        if (name is null || name.IndexOfAny(['`', '[']) >= 0)
        {
            throw new NotSupportedException(
                $"A default of the enum '{type.Name}' has no literal form the tooling can write: it is declared inside a " +
                "generic type, which a generated file cannot name without its type arguments. Write the change by hand.");
        }

        // A nested type's name joins its declaring type with '+', where C# writes a '.'.
        name = name.Replace('+', '.');
        if (Enum.IsDefined(type, member))
        {
            return $"{name}.{member}";
        }

        // The underlying number in decimal; a negative one is parenthesised, since (T)-1 reads as a subtraction.
        var number = member.ToString("D");
        return number.StartsWith("-", StringComparison.Ordinal) ? $"({name})({number})" : $"({name}){number}";
    }

    /// <summary>The text between <paramref name="quote" />s, every character a literal cannot hold as it is escaped.</summary>
    private static string Quoted(string value, char quote)
    {
        var text = new StringBuilder(value.Length + 2).Append(quote);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    text.Append(@"\\");
                    break;
                case '"' or '\'' when character == quote:
                    text.Append('\\').Append(character);
                    break;
                case '\n':
                    text.Append(@"\n");
                    break;
                case '\r':
                    text.Append(@"\r");
                    break;
                case '\t':
                    text.Append(@"\t");
                    break;
                case '\0':
                    text.Append(@"\0");
                    break;
                default:
                    if (char.IsControl(character) || char.IsSurrogate(character) || character is '\u0085' or '\u2028' or '\u2029')
                    {
                        text.Append(@"\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        text.Append(character);
                    }

                    break;
            }
        }

        return text.Append(quote).ToString();
    }
}
