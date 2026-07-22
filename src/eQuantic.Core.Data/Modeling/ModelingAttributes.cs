using System;

namespace eQuantic.Core.Data.Modeling;

/// <summary>
///     The store-neutral modeling vocabulary: <b>one</b> set of eQuantic-owned annotations that every provider
///     interprets — no driver attributes on entities (<c>[BsonElement]</c>, EF's <c>[Column]</c>…), no rewrite
///     when an entity moves between stores. The names are deliberately distinct from
///     <c>System.ComponentModel.DataAnnotations</c> so both can coexist without ambiguity.
///     <para>
///         Precedence is explicit and deterministic: <b>conventions &lt; annotations &lt; fluent</b> — the
///         annotations seed each provider's model builder, and fluent configuration overrides them. Each
///         provider honours the subset that maps to its store (the model's <c>Explain()</c> shows the outcome);
///         annotations outside a store's vocabulary are ignored, never errors.
///     </para>
/// </summary>
/// <remarks>Applies to the class: the storage name — table, collection or container.</remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class EntityAttribute(string name) : Attribute
{
    /// <summary>The storage name.</summary>
    public string Name { get; } = name;

    /// <summary>The storage name declared for <paramref name="entityType" />, or <c>null</c>.</summary>
    public static string? NameFor(Type entityType) =>
        (GetCustomAttribute(entityType, typeof(EntityAttribute)) as EntityAttribute)?.Name;
}

/// <summary>Declares the entity's key member (a member named <c>Id</c> is the convention otherwise).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EntityKeyAttribute : Attribute
{
    /// <summary>Whether the store generates the key (identity): inserts omit it and read it back where supported.</summary>
    public bool Generated { get; set; }
}

/// <summary>
///     The member's stored name — a column (relational), a document field (MongoDB) — when the provider's
///     naming convention does not fit.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class StoredAsAttribute(string name) : Attribute
{
    /// <summary>The stored name.</summary>
    public string Name { get; } = name;
}

/// <summary>Excludes the member from the mapping (navigations are excluded automatically; this is for the rest).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class UnmappedAttribute : Attribute;

/// <summary>
///     Declares the member part of the <b>partition key</b> — the access-pattern declaration: Cassandra's
///     partition key and Cosmos DB's partition key path (compose with <see cref="Order" /> — Cosmos builds a
///     hierarchical, multi-hash key from up to three members). Relational stores and MongoDB have no partition
///     concept and ignore it.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PartitionKeyAttribute : Attribute
{
    /// <summary>The member's position in a composite partition key.</summary>
    public int Order { get; set; }
}

/// <summary>
///     Declares an ordered-read member — "I read this sorted, within the key" — and each store materializes the
///     declaration as well as it can: Cassandra as a real clustering key (rows physically ordered in the
///     partition), relational stores and MongoDB as a multi-column index with the declared directions, Cosmos DB
///     as a composite index on the container's policy (two or more members). The semantics of queries never
///     change; only the plan does.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ClusteringKeyAttribute : Attribute
{
    /// <summary>The member's position among the clustering keys.</summary>
    public int Order { get; set; }

    /// <summary>Whether the clustering order is descending.</summary>
    public bool Descending { get; set; }
}

/// <summary>
///     Declares storage facets for a member — a maximum <see cref="Length" /> for text, a
///     <see cref="Precision" />/<see cref="Scale" /> for decimals. Relational DDL sizes the column with them
///     (<c>varchar(n)</c>, <c>numeric(p,s)</c>); stores without sized types ignore them.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class FacetAttribute : Attribute
{
    /// <summary>The maximum text length (0 = the store's default, usually unbounded).</summary>
    public int Length { get; set; }

    /// <summary>The total number of significant digits (0 = the store's default).</summary>
    public int Precision { get; set; }

    /// <summary>The number of digits after the decimal point.</summary>
    public int Scale { get; set; }
}

/// <summary>
///     Declares the optimistic-concurrency token: the versioned column on relational stores
///     (<c>WHERE … AND version = @old</c>, bumped on every write), the <c>_etag</c> member on Cosmos DB
///     (conditional <c>If-Match</c> replace).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ConcurrencyTokenAttribute : Attribute;

/// <summary>Declares a Cassandra <c>counter</c> column (the table mutates through increments, never inserts).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class CounterAttribute : Attribute;

/// <summary>What a search index can match — and therefore which <c>LIKE</c> shapes push down.</summary>
public enum SearchMode
{
    /// <summary>Substring matching (<c>StartsWith</c>, <c>EndsWith</c>, <c>Contains</c> and any pattern).</summary>
    Contains,

    /// <summary>Prefix matching only (<c>StartsWith</c>).</summary>
    Prefix,
}

/// <summary>
///     Declares a search-indexed text member: <c>StartsWith</c>/<c>EndsWith</c>/<c>Contains</c> and
///     <c>Db.Like</c> push down natively on it (Cassandra SASI; the migration creates the index).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SearchIndexAttribute : Attribute
{
    /// <summary>The matching mode (<see cref="SearchMode.Contains" /> by default).</summary>
    public SearchMode Mode { get; set; } = SearchMode.Contains;
}

/// <summary>Declares the entity's default time-to-live (Cosmos DB container TTL).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class TimeToLiveAttribute(int seconds) : Attribute
{
    /// <summary>The default TTL, in seconds.</summary>
    public int Seconds { get; } = seconds;
}
