using System.Collections.Generic;
using System.Linq;

namespace eQuantic.Core.Data.Evolution;

/// <summary>What a comparison found between two versions of a model.</summary>
public enum ModelChangeKind
{
    /// <summary>An entity that was not mapped before.</summary>
    AddCollection,

    /// <summary>An entity that is no longer mapped.</summary>
    DropCollection,

    /// <summary>The same entity, stored under a different name.</summary>
    RenameCollection,

    /// <summary>A member that was not mapped before.</summary>
    AddField,

    /// <summary>A member that is no longer mapped.</summary>
    DropField,

    /// <summary>The same member, stored under a different name.</summary>
    RenameField,

    /// <summary>The same member, stored as a different type.</summary>
    ConvertField,

    /// <summary>The same member and type, sized differently.</summary>
    ChangeFacets,
}

/// <summary>
///     One difference between two versions of a model, as data. It is deliberately not an executable operation:
///     what a comparison produces is a change to be <b>written into a file</b>, reviewed, and only then run.
/// </summary>
/// <param name="Kind">What changed.</param>
/// <param name="EntityType">The entity's CLR type name.</param>
public sealed record ModelChange(ModelChangeKind Kind, string EntityType)
{
    /// <summary>The member involved, when the change is about one.</summary>
    public string? Member { get; init; }

    /// <summary>What it was — a stored name, a type — when the change replaces something.</summary>
    public string? From { get; init; }

    /// <summary>What it becomes.</summary>
    public string? To { get; init; }

    /// <summary>
    ///     The value existing records take, as a C# literal, when the change adds a member and the model declared
    ///     one. <c>null</c> together with <see cref="NeedsValue" /> means the change cannot run until it is given.
    /// </summary>
    public string? DefaultLiteral { get; init; }

    /// <summary>
    ///     Whether the change adds a member without saying what existing records should hold. The change is still
    ///     produced — leaving it out would hide the problem — but it is written so that running it fails until the
    ///     value is supplied, because the alternative is every existing record quietly taking <c>default(T)</c>.
    /// </summary>
    public bool NeedsValue { get; init; }

    /// <summary>
    ///     Set when a member disappeared and another appeared and nothing said they are the same one. The change
    ///     is generated as a drop and an add, which loses the data — declaring <c>[PreviousName]</c> on the new
    ///     member turns the pair into a rename that keeps it.
    /// </summary>
    public string? AmbiguousRenameHint { get; init; }
}

/// <summary>
///     A change the store cannot make, named with what to do instead. Refusing beats generating a statement that
///     the database will reject — or worse, one that silently destroys data on the way.
/// </summary>
/// <param name="EntityType">The entity's CLR type name.</param>
/// <param name="Reason">Why it cannot be done.</param>
/// <param name="Alternative">What to do instead.</param>
public sealed record ModelRefusal(string EntityType, string Reason, string Alternative);

/// <summary>The full result of a comparison: what changed, and what the store will not do.</summary>
/// <param name="Provider">The store both versions belong to.</param>
/// <param name="Changes">The changes, in the order they must be applied.</param>
/// <param name="Refusals">What cannot be generated, and why.</param>
public sealed record ModelDifference(
    string Provider,
    IReadOnlyList<ModelChange> Changes,
    IReadOnlyList<ModelRefusal> Refusals)
{
    /// <summary>Whether the model and the snapshot agree — nothing to generate.</summary>
    public bool IsEmpty => Changes.Count == 0 && Refusals.Count == 0;

    /// <summary>Whether any change needs a value before it can run.</summary>
    public bool HasUnansweredValues => Changes.Any(change => change.NeedsValue);
}
