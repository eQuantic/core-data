using eQuantic.Core.Data.Analyzers;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace eQuantic.Core.Data.Analyzers.Tests;

/// <summary>
///     Proves every EQD diagnostic — one misuse each — plus the clean-model negative. The modeling attributes
///     are compiled from stubs (same metadata names), so the tests need no reference to the data packages —
///     exactly how the analyzer itself works.
/// </summary>
[TestFixture]
public sealed class ModelingAnalyzerTests
{
    /// <summary>The modeling vocabulary, as the analyzer sees it (matched by full metadata name).</summary>
    private const string Stubs = """
        using System;

        namespace eQuantic.Core.Data.Modeling
        {
            [AttributeUsage(AttributeTargets.Class)]
            public sealed class EntityAttribute : Attribute { public EntityAttribute(string name) { } }

            [AttributeUsage(AttributeTargets.Property)]
            public sealed class EntityKeyAttribute : Attribute { public bool Generated { get; set; } }

            [AttributeUsage(AttributeTargets.Property)]
            public sealed class StoredAsAttribute : Attribute { public StoredAsAttribute(string name) { } }

            [AttributeUsage(AttributeTargets.Property)]
            public sealed class UnmappedAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Property)]
            public sealed class PartitionKeyAttribute : Attribute { public int Order { get; set; } }

            [AttributeUsage(AttributeTargets.Property)]
            public sealed class ClusteringKeyAttribute : Attribute { public int Order { get; set; } public bool Descending { get; set; } }

            [AttributeUsage(AttributeTargets.Property)]
            public sealed class ConcurrencyTokenAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Property)]
            public sealed class CounterAttribute : Attribute { }

            public enum SearchMode { Contains, Prefix }

            [AttributeUsage(AttributeTargets.Property)]
            public sealed class SearchIndexAttribute : Attribute { public SearchMode Mode { get; set; } }

            [AttributeUsage(AttributeTargets.Class)]
            public sealed class TimeToLiveAttribute : Attribute { public TimeToLiveAttribute(int seconds) { } }

            [AttributeUsage(AttributeTargets.Property)]
            public sealed class FacetAttribute : Attribute { public int Length { get; set; } public int Precision { get; set; } public int Scale { get; set; } }
        }

        """;

    private static Task Verify(string source, params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<ModelingAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.TestState.Sources.Add(Stubs);   // the vocabulary compiles as its own file beside the test code
        test.ExpectedDiagnostics.AddRange(expected);
        return test.RunAsync();
    }

    [Test]
    public Task A_clean_model_reports_nothing() => Verify("""
        using eQuantic.Core.Data.Modeling;

        [Entity("sale_orders")]
        [TimeToLive(3600)]
        public sealed class SaleOrder
        {
            [EntityKey(Generated = true)] public long Id { get; set; }
            [StoredAs("client_name")] public string Name { get; set; } = "";
            [Facet(Length = 200)] public string Title { get; set; } = "";
            [Facet(Precision = 18, Scale = 2)] public decimal Total { get; set; }
            [ConcurrencyToken] public int Revision { get; set; }
            [PartitionKey(Order = 0)] public string Tenant { get; set; } = "";
            [PartitionKey(Order = 1)] public string Region { get; set; } = "";
            [ClusteringKey(Order = 0)] public string Kind { get; set; } = "";
            [ClusteringKey(Order = 1, Descending = true)] public int Magnitude { get; set; }
            [SearchIndex] public string Description { get; set; } = "";
            [Counter] public long Hits { get; set; }
            [Unmapped] public string Scratch { get; set; } = "";
        }
        """);

    [Test]
    public Task EQD001_reports_a_token_type_no_provider_can_version() => Verify("""
        using eQuantic.Core.Data.Modeling;

        public sealed class Order
        {
            [{|#0:ConcurrencyToken|}] public decimal Revision { get; set; }
        }
        """,
        new DiagnosticResult(ModelingDiagnostics.ConcurrencyTokenType).WithLocation(0)
            .WithArguments("Revision", "decimal"));

    [Test]
    public Task EQD002_reports_partition_members_sharing_an_order() => Verify("""
        using eQuantic.Core.Data.Modeling;

        public sealed class Event
        {
            [{|#0:PartitionKey|}] public string Tenant { get; set; } = "";
            [{|#1:PartitionKey|}] public string Region { get; set; } = "";
        }
        """,
        new DiagnosticResult(ModelingDiagnostics.DuplicatePartitionKeyOrder).WithLocation(0).WithArguments("Event", 0),
        new DiagnosticResult(ModelingDiagnostics.DuplicatePartitionKeyOrder).WithLocation(1).WithArguments("Event", 0));

    [Test]
    public Task EQD003_reports_clustering_members_sharing_an_order() => Verify("""
        using eQuantic.Core.Data.Modeling;

        public sealed class Event
        {
            [{|#0:ClusteringKey(Order = 2)|}] public string Kind { get; set; } = "";
            [{|#1:ClusteringKey(Order = 2)|}] public int Magnitude { get; set; }
        }
        """,
        new DiagnosticResult(ModelingDiagnostics.DuplicateClusteringKeyOrder).WithLocation(0).WithArguments("Event", 2),
        new DiagnosticResult(ModelingDiagnostics.DuplicateClusteringKeyOrder).WithLocation(1).WithArguments("Event", 2));

    [Test]
    public Task EQD004_reports_a_second_entity_key() => Verify("""
        using eQuantic.Core.Data.Modeling;

        public sealed class Order
        {
            [EntityKey] public long Id { get; set; }
            [{|#0:EntityKey|}] public long Code { get; set; }
        }
        """,
        new DiagnosticResult(ModelingDiagnostics.MultipleEntityKeys).WithLocation(0).WithArguments("Order"));

    [Test]
    public Task EQD005_reports_a_non_positive_ttl() => Verify("""
        using eQuantic.Core.Data.Modeling;

        [{|#0:TimeToLive(0)|}]
        public sealed class Session
        {
            public string Id { get; set; } = "";
        }
        """,
        new DiagnosticResult(ModelingDiagnostics.InvalidTimeToLive).WithLocation(0).WithArguments("Session", 0));

    [Test]
    public Task EQD006_reports_scale_beyond_precision_and_misplaced_length() => Verify("""
        using eQuantic.Core.Data.Modeling;

        public sealed class Invoice
        {
            [{|#0:Facet(Precision = 4, Scale = 6)|}] public decimal Total { get; set; }
            [{|#1:Facet(Length = 50)|}] public int Quantity { get; set; }
        }
        """,
        new DiagnosticResult(ModelingDiagnostics.InvalidFacet).WithLocation(0)
            .WithArguments("Total", "Scale (6) cannot exceed Precision (4)"),
        new DiagnosticResult(ModelingDiagnostics.InvalidFacet).WithLocation(1)
            .WithArguments("Quantity", "Length applies to string members and 'int' is not one"));

    [Test]
    public Task EQD007_reports_an_unmapped_member_with_mapping_annotations() => Verify("""
        using eQuantic.Core.Data.Modeling;

        public sealed class Order
        {
            [Unmapped]
            [StoredAs("scratch")]
            public string {|#0:Scratch|} { get; set; } = "";
        }
        """,
        new DiagnosticResult(ModelingDiagnostics.UnmappedConflict).WithLocation(0)
            .WithArguments("Scratch", "StoredAs"));

    [Test]
    public Task EQD008_reports_a_search_index_on_a_non_string() => Verify("""
        using eQuantic.Core.Data.Modeling;

        public sealed class Reading
        {
            [{|#0:SearchIndex|}] public int Value { get; set; }
        }
        """,
        new DiagnosticResult(ModelingDiagnostics.SearchIndexOnNonString).WithLocation(0)
            .WithArguments("Value", "int"));

    [Test]
    public Task EQD009_reports_a_counter_on_a_non_integral() => Verify("""
        using eQuantic.Core.Data.Modeling;

        public sealed class Tally
        {
            [{|#0:Counter|}] public string Hits { get; set; } = "";
        }
        """,
        new DiagnosticResult(ModelingDiagnostics.CounterOnNonIntegral).WithLocation(0)
            .WithArguments("Hits", "string"));

    [Test]
    public Task EQD010_reports_a_generated_key_the_store_cannot_generate() => Verify("""
        using System;
        using eQuantic.Core.Data.Modeling;

        public sealed class Order
        {
            [{|#0:EntityKey(Generated = true)|}] public Guid Id { get; set; }
        }
        """,
        new DiagnosticResult(ModelingDiagnostics.GeneratedKeyType).WithLocation(0)
            .WithArguments("Id", "System.Guid"));

    [Test]
    public Task EQD011_reports_empty_storage_names() => Verify("""
        using eQuantic.Core.Data.Modeling;

        [{|#0:Entity("")|}]
        public sealed class Order
        {
            [{|#1:StoredAs("")|}] public string Name { get; set; } = "";
        }
        """,
        new DiagnosticResult(ModelingDiagnostics.EmptyStorageName).WithLocation(0).WithArguments("Order", "Entity"),
        new DiagnosticResult(ModelingDiagnostics.EmptyStorageName).WithLocation(1).WithArguments("Name", "StoredAs"));

    [Test]
    public Task Nullable_token_types_unwrap_before_the_check() => Verify("""
        using eQuantic.Core.Data.Modeling;

        public sealed class Order
        {
            [ConcurrencyToken] public long? Revision { get; set; }
        }
        """);
}
