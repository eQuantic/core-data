using System.Collections.Generic;
using System.Linq;

namespace eQuantic.Core.Data.Evolution;

/// <summary>
///     What the model looked like when the last change was generated — the other half of a comparison, since a
///     model on its own only says what it is now, never what it was.
///     <para>
///         The shape is store-neutral and deliberately a superset: a relational model fills in columns and
///         facets, Cassandra fills in partition and clustering keys, Cosmos DB fills in paths and a time to live.
///         What a store has no concept of stays empty, which is also how a comparison knows not to ask about it.
///     </para>
/// </summary>
/// <param name="Provider">The provider that produced it, so a snapshot is never compared against another store's.</param>
/// <param name="Entities">The mapped entities, ordered by type name so the file is stable across regenerations.</param>
public sealed record ModelSnapshot(string Provider, IReadOnlyList<EntitySnapshot> Entities)
{
    /// <summary>An empty snapshot — what a model is compared against the first time.</summary>
    public static ModelSnapshot Empty(string provider) => new(provider, []);

    /// <summary>The snapshot of an entity, by CLR type name, or <c>null</c> when it is not in there.</summary>
    /// <param name="entityType">The entity's CLR type name.</param>
    public EntitySnapshot? For(string entityType) =>
        Entities.FirstOrDefault(entity => entity.EntityType == entityType);
}

/// <summary>
///     One mapped entity as it was stored. Identity is the <see cref="EntityType" />: renaming the table is a
///     change to the same entity, while a different type is a different entity.
/// </summary>
/// <param name="EntityType">The entity's CLR type name (the identity across versions).</param>
/// <param name="Collection">The stored name — a table, a collection, a container.</param>
/// <param name="Fields">The mapped members, ordered by member name.</param>
public sealed record EntitySnapshot(string EntityType, string Collection, IReadOnlyList<FieldSnapshot> Fields)
{
    /// <summary>The key members, in declared order.</summary>
    public IReadOnlyList<string> Keys { get; init; } = [];

    /// <summary>Whether the key is store-generated.</summary>
    public bool KeyIsGenerated { get; init; }

    /// <summary>The partition-key members, in declared order (Cassandra, Cosmos DB).</summary>
    public IReadOnlyList<string> PartitionKeys { get; init; } = [];

    /// <summary>The ordered-read members, in declared order.</summary>
    public IReadOnlyList<ClusteringSnapshot> Clustering { get; init; } = [];

    /// <summary>The optimistic-concurrency member, or <c>null</c>.</summary>
    public string? ConcurrencyField { get; init; }

    /// <summary>The declared time to live in seconds, or <c>null</c>.</summary>
    public int? TimeToLiveSeconds { get; init; }

    /// <summary>The search-indexed members and what each promises to match.</summary>
    public IReadOnlyList<SearchSnapshot> Search { get; init; } = [];

    /// <summary>The snapshot of a member, by CLR member name, or <c>null</c> when it is not in there.</summary>
    /// <param name="member">The member name.</param>
    public FieldSnapshot? Field(string member) =>
        Fields.FirstOrDefault(field => field.Member == member);
}

/// <summary>
///     One mapped member as it was stored. Identity is the <see cref="Member" /> — the CLR name — so a change to
///     <see cref="Name" /> is a rename of the same member rather than a different one.
/// </summary>
/// <param name="Member">The CLR member name (the identity across versions).</param>
/// <param name="Name">The stored name — a column, a document element.</param>
/// <param name="StoredType">The stored CLR type's full name.</param>
public sealed record FieldSnapshot(string Member, string Name, string StoredType)
{
    /// <summary>The maximum text length (0 = the store's default).</summary>
    public int Length { get; init; }

    /// <summary>The decimal precision (0 = the store's default).</summary>
    public int Precision { get; init; }

    /// <summary>The decimal scale.</summary>
    public int Scale { get; init; }

    /// <summary>Whether the member is nullable as stored.</summary>
    public bool Nullable { get; init; }

    /// <summary>
    ///     The stored names this member used to have, from <c>[PreviousName]</c>. A comparison reads them on the
    ///     current model to recognise a rename; on a past snapshot they are simply history.
    /// </summary>
    public IReadOnlyList<string> PreviousNames { get; init; } = [];

    /// <summary>
    ///     The value existing records take when the member is added, already rendered as a C# literal, or
    ///     <c>null</c> when none was declared — which is what makes a comparison refuse to add it silently.
    /// </summary>
    public string? DefaultLiteral { get; init; }
}

/// <summary>An ordered-read member and its direction.</summary>
/// <param name="Member">The member name.</param>
/// <param name="Descending">Whether the order is descending.</param>
public sealed record ClusteringSnapshot(string Member, bool Descending);

/// <summary>A search-indexed member and what the declaration promises to match.</summary>
/// <param name="Member">The member name.</param>
/// <param name="Mode">The search mode's name.</param>
public sealed record SearchSnapshot(string Member, string Mode);
