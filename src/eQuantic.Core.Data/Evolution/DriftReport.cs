using System.Collections.Generic;
using System.Linq;

namespace eQuantic.Core.Data.Evolution;

/// <summary>A way the store and the model disagree.</summary>
public enum DriftKind
{
    /// <summary>The model maps a collection the store does not have.</summary>
    MissingCollection,

    /// <summary>The model maps a field the collection does not have.</summary>
    MissingField,

    /// <summary>The collection has a field the model does not map.</summary>
    UnexpectedField,

    /// <summary>The field is stored as a different type.</summary>
    TypeDiffers,

    /// <summary>The field accepts a missing value where the model does not, or the other way round.</summary>
    NullabilityDiffers,
}

/// <summary>One disagreement, said plainly enough to act on.</summary>
/// <param name="Kind">How they disagree.</param>
/// <param name="EntityType">The CLR type mapped to the collection.</param>
/// <param name="Collection">The collection's stored name.</param>
public sealed record DriftFinding(DriftKind Kind, string EntityType, string Collection)
{
    /// <summary>The field, when the finding is about one.</summary>
    public string? Field { get; init; }

    /// <summary>What the model says.</summary>
    public string? Expected { get; init; }

    /// <summary>What the store has.</summary>
    public string? Found { get; init; }

    /// <summary>Whether this finding will stop the application working, rather than merely being untidy.</summary>
    /// <remarks>
    ///     A field the model does not map is the one kind that usually is not a fault: databases get shared, and
    ///     another application's column is not this one's problem. Everything else is read on every query.
    /// </remarks>
    public bool Breaks => Kind != DriftKind.UnexpectedField;
}

/// <summary>What a drift check found.</summary>
/// <param name="Provider">The store.</param>
/// <param name="Findings">The disagreements.</param>
public sealed record DriftReport(string Provider, IReadOnlyList<DriftFinding> Findings)
{
    /// <summary>Whether the store matches the model in every respect that was checked.</summary>
    public bool IsClean => Findings.Count == 0;

    /// <summary>Whether anything found will stop the application working.</summary>
    public bool Breaks => Findings.Any(finding => finding.Breaks);
}
