using Microsoft.CodeAnalysis;

namespace eQuantic.Core.Data.Analyzers;

/// <summary>
///     The compile-time contract of the modeling vocabulary: every rule here is a <b>universal</b> truth of the
///     annotations — wrong on every provider — surfaced while typing instead of at model build. Provider-relative
///     rules (what one store supports and another does not) deliberately stay at runtime, where the provider is
///     known and the message can be exact.
/// </summary>
public static class ModelingDiagnostics
{
    private const string Category = "eQuantic.Modeling";
    private const string HelpUri = "https://equantic.github.io/core-data/guides/modeling/annotations.html";

    private static DiagnosticDescriptor Rule(string id, string title, string message) =>
        new(id, title, message, Category, DiagnosticSeverity.Warning, isEnabledByDefault: true, helpLinkUri: HelpUri);

    /// <summary>EQD001 — a concurrency token of a type no provider can version.</summary>
    public static readonly DiagnosticDescriptor ConcurrencyTokenType = Rule(
        "EQD001",
        "Concurrency token type is not versionable",
        "The concurrency token '{0}' must be int, long, Guid or string; '{1}' cannot version writes on any provider");

    /// <summary>EQD002 — a composite partition key whose member order is ambiguous.</summary>
    public static readonly DiagnosticDescriptor DuplicatePartitionKeyOrder = Rule(
        "EQD002",
        "Composite partition key has ambiguous member order",
        "'{0}' declares several [PartitionKey] members sharing Order {1}; set explicit, distinct Order values so the composition is deterministic");

    /// <summary>EQD003 — clustering keys whose member order is ambiguous.</summary>
    public static readonly DiagnosticDescriptor DuplicateClusteringKeyOrder = Rule(
        "EQD003",
        "Clustering keys have ambiguous member order",
        "'{0}' declares several [ClusteringKey] members sharing Order {1}; set explicit, distinct Order values so the ordering is deterministic");

    /// <summary>EQD004 — more than one <c>[EntityKey]</c> member.</summary>
    public static readonly DiagnosticDescriptor MultipleEntityKeys = Rule(
        "EQD004",
        "Multiple [EntityKey] members",
        "'{0}' annotates more than one member with [EntityKey]; only one wins — declare a composite key with Key(x => new {{ … }}) in the fluent model instead");

    /// <summary>EQD005 — a non-positive time-to-live.</summary>
    public static readonly DiagnosticDescriptor InvalidTimeToLive = Rule(
        "EQD005",
        "Time-to-live must be positive",
        "[TimeToLive] on '{0}' declares {1} second(s); the lifetime must be positive");

    /// <summary>EQD006 — an invalid or misplaced facet.</summary>
    public static readonly DiagnosticDescriptor InvalidFacet = Rule(
        "EQD006",
        "Invalid [Facet]",
        "The [Facet] on '{0}' is invalid: {1}");

    /// <summary>EQD007 — an excluded member carrying mapping annotations.</summary>
    public static readonly DiagnosticDescriptor UnmappedConflict = Rule(
        "EQD007",
        "[Unmapped] member carries mapping annotations",
        "'{0}' is [Unmapped] but also annotated with [{1}]; an excluded member cannot carry mapping annotations");

    /// <summary>EQD008 — a search index on a member LIKE cannot serve.</summary>
    public static readonly DiagnosticDescriptor SearchIndexOnNonString = Rule(
        "EQD008",
        "[SearchIndex] requires a string member",
        "[SearchIndex] on '{0}' requires a string member; '{1}' cannot serve substring matches");

    /// <summary>EQD009 — a counter on a non-integral member.</summary>
    public static readonly DiagnosticDescriptor CounterOnNonIntegral = Rule(
        "EQD009",
        "[Counter] requires an integral member",
        "[Counter] on '{0}' requires an integral member (it stores as a Cassandra counter/bigint); '{1}' is not integral");

    /// <summary>EQD010 — a database-generated key of a type stores cannot generate.</summary>
    public static readonly DiagnosticDescriptor GeneratedKeyType = Rule(
        "EQD010",
        "Generated key type is not identity-capable",
        "[EntityKey(Generated = true)] on '{0}' requires an integral member (an identity column); the store cannot generate '{1}'");

    /// <summary>EQD011 — an empty storage name.</summary>
    public static readonly DiagnosticDescriptor EmptyStorageName = Rule(
        "EQD011",
        "Empty storage name",
        "The storage name on '{0}' is empty; give [{1}] a real name or remove the attribute");
}
