using System;
using eQuantic.Core.Data.Modeling;

namespace eQuantic.Core.Data.Evolution;

/// <summary>
///     How a snapshot spells what the CLR type of a member does not say about its stored values: an enum stored as
///     its labels, and a member whose value the store assigns.
///     <para>
///         Both change what is in the store without changing the member's type. An enum switched from its numbers to
///         its labels has the same CLR type on both sides, so a snapshot that recorded only the type would compare it
///         as unchanged while every stored number became unreadable; a member that becomes store-generated keeps its
///         type while the store has to start assigning it. Spelling both in the snapshot is what lets a comparison
///         see them, and every provider spells them alike, so they mean the same thing on every engine.
///     </para>
/// </summary>
public static class StoredForm
{
    /// <summary>What a stored type starts with when the member is an enum stored as its labels.</summary>
    public const string LabelsPrefix = "labels:";

    /// <summary>What a stored type ends with when the member is a collection of such an enum.</summary>
    public const string CollectionSuffix = "[]";

    /// <summary>The generation of a member the store numbers from a sequence.</summary>
    public const string Sequence = "sequence";

    /// <summary>The generation of a member the store stamps with its clock.</summary>
    public const string Clock = "clock";

    /// <summary>What the generation of a member a relational store fills with a SQL default starts with.</summary>
    public const string DefaultPrefix = "default ";

    /// <summary>The stored type of a member holding <paramref name="enumType" /> stored as its labels: <c>labels:Shop.State</c>.</summary>
    /// <param name="enumType">The enum.</param>
    public static string Labels(Type enumType) => LabelsPrefix + (enumType.FullName ?? enumType.Name);

    /// <summary>
    ///     The stored type of a member holding a collection of <paramref name="enumType" /> stored as its labels:
    ///     <c>labels:Shop.State[]</c>, whatever the collection's own type.
    /// </summary>
    /// <param name="enumType">The enum.</param>
    public static string LabelCollection(Type enumType) => Labels(enumType) + CollectionSuffix;

    /// <summary>
    ///     The full name of the enum a stored type holds as labels (alone or as a collection's elements), or
    ///     <c>null</c> when it holds anything else.
    /// </summary>
    /// <param name="storedType">A snapshot's stored type.</param>
    public static string? LabelsOf(string? storedType)
    {
        if (storedType is null || !storedType.StartsWith(LabelsPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var name = storedType[LabelsPrefix.Length..];
        return name.EndsWith(CollectionSuffix, StringComparison.Ordinal) ? name[..^CollectionSuffix.Length] : name;
    }

    /// <summary>Whether a stored type is a collection whose elements are an enum's labels.</summary>
    /// <param name="storedType">A snapshot's stored type.</param>
    public static bool IsLabelCollection(string? storedType) =>
        LabelsOf(storedType) is not null && storedType!.EndsWith(CollectionSuffix, StringComparison.Ordinal);

    /// <summary>The generation of a member the store assigns <paramref name="value" /> to: <c>sequence</c> or <c>clock</c>.</summary>
    /// <param name="value">What the store assigns, resolved.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value" /> is <see cref="StoreValue.Inferred" />.</exception>
    public static string Generated(StoreValue value) => value switch
    {
        StoreValue.Sequence => Sequence,
        StoreValue.Clock => Clock,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value,
            "A snapshot records what the store assigns once it is resolved: a sequence or the clock."),
    };

    /// <summary>The generation of a member a relational store fills with <paramref name="sql" />: <c>default now()</c>.</summary>
    /// <param name="sql">The default expression, as SQL.</param>
    public static string GeneratedDefault(string sql) => DefaultPrefix + sql;

    /// <summary>The SQL default of a generation, or <c>null</c> when it is a sequence, the clock, or none.</summary>
    /// <param name="generated">A snapshot's generation.</param>
    public static string? DefaultOf(string? generated) =>
        generated is not null && generated.StartsWith(DefaultPrefix, StringComparison.Ordinal)
            ? generated[DefaultPrefix.Length..]
            : null;
}
