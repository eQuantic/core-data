using System;
using System.Text.RegularExpressions;

namespace eQuantic.Core.Data.Query;

/// <summary>
///     Query function markers — call them inside a filter lambda and the interpreter translates them to the
///     store's native function (<c>LIKE</c>, <c>EXTRACT</c>/<c>YEAR</c>, …) through the dialect's function
///     registry. Every marker also has a <b>real C# implementation with the same semantics</b>, so a dialect
///     that has no translation degrades the clause to the gated client-side residual instead of failing —
///     the same rule applies to the markers you write yourself: give them a real body, register a translation
///     with <c>Functions.Map(...)</c>, and they behave exactly like these.
/// </summary>
public static class Db
{
    /// <summary>
    ///     SQL <c>LIKE</c> with a <b>raw</b> pattern — <c>%</c> and <c>_</c> are wildcards under your control
    ///     (unlike <c>StartsWith</c>/<c>Contains</c>, which escape them). Case sensitivity follows the store's
    ///     collation; the client-side fallback is case-sensitive.
    /// </summary>
    /// <param name="value">The member value.</param>
    /// <param name="pattern">The LIKE pattern.</param>
    public static bool Like(string? value, string pattern) =>
        value is not null && Regex.IsMatch(value,
            "^" + Regex.Escape(pattern).Replace("%", ".*").Replace("_", ".") + "$", RegexOptions.Singleline);

    /// <summary>The year component of the value.</summary>
    /// <param name="value">The member value.</param>
    public static int Year(DateTime value) => value.Year;

    /// <summary>The month component of the value.</summary>
    /// <param name="value">The member value.</param>
    public static int Month(DateTime value) => value.Month;

    /// <summary>The day component of the value.</summary>
    /// <param name="value">The member value.</param>
    public static int Day(DateTime value) => value.Day;
}
