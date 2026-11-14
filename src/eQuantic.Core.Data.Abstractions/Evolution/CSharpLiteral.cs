using System;
using System.Globalization;
using System.Text;

namespace eQuantic.Core.Data.Evolution;

/// <summary>
///     Renders a declared value as the C# literal that will appear in a generated file. It covers the types a
///     default is actually written as; anything else is refused by name rather than rendered into something that
///     compiles but means something different. Text compiles and reads back as the same text whatever it holds:
///     quotes, backslashes, line breaks (a SQL default written over several lines), tabs, any other control character,
///     the characters C# reads as the end of a line (U+0085, U+2028, U+2029) and surrogates are escaped.
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
