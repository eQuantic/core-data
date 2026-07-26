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

    /// <summary>The store distributes the data by something other than what the model says.</summary>
    PartitionKeyDiffers,

    /// <summary>The model declares an index the collection does not carry.</summary>
    MissingIndex,

    /// <summary>The collection carries an index the model does not declare.</summary>
    UnexpectedIndex,

    /// <summary>The index is there, on different members.</summary>
    IndexDiffers,
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

    /// <summary>
    ///     Whether an index finding is about the one index that decides whether documents expire. Set only for
    ///     that, because it is the only index whose absence changes what the store holds.
    /// </summary>
    public bool ExpiresDocuments { get; init; }

    /// <summary>Whether this finding will stop the application working, rather than merely being untidy.</summary>
    /// <remarks>
    ///     Three kinds are reported without being faults. A field the model does not map belongs to somebody else —
    ///     databases get shared. And a missing or differing index changes how fast a query runs, not whether it
    ///     answers — with one exception, the index that expires documents, whose absence means data that should
    ///     have been deleted is still there.
    /// </remarks>
    public bool Breaks => Kind switch
    {
        DriftKind.UnexpectedField or DriftKind.UnexpectedIndex => false,
        DriftKind.MissingIndex or DriftKind.IndexDiffers => ExpiresDocuments,
        _ => true,
    };

    /// <summary>
    ///     Whether closing this difference needs the data moved rather than the schema altered. A partition key is
    ///     fixed at creation, so there is no migration for it — only a new collection and a copy.
    /// </summary>
    public bool NeedsRebuild => Kind == DriftKind.PartitionKeyDiffers;
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

    /// <summary>Whether anything found cannot be migrated at all, only rebuilt.</summary>
    public bool NeedsRebuild => Findings.Any(finding => finding.NeedsRebuild);
}
