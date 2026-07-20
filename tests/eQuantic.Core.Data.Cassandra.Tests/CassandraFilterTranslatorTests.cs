using System.Linq.Expressions;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     Unit tests for the hybrid CQL filter translator — pure expression analysis, no Cassandra required. Proves
///     that key predicates translate natively, non-key predicates flag <c>ALLOW FILTERING</c>, and the shapes CQL
///     cannot express are rejected.
/// </summary>
[TestFixture]
public sealed class CassandraFilterTranslatorTests
{
    private static CassandraEntityConfiguration Config() =>
        new CassandraModelBuilder()
            .Entity<OrderData>(entity => entity
                .Table("orders")
                .PartitionKey(x => x.TenantId)
                .ClusteringKey(x => x.CreatedAt, descending: true))
            .Build()
            .For(typeof(OrderData));

    private static CassandraWhere Translate(Expression<Func<OrderData, bool>> filter) =>
        CassandraFilterTranslator.Translate(Config(), filter);

    [Test]
    public void Translates_partition_key_equality_natively()
    {
        var where = Translate(x => x.TenantId == 5);

        Assert.That(where.Cql, Is.EqualTo("TenantId = ?"));
        Assert.That(where.Parameters, Is.EqualTo(new object?[] { 5 }));
        Assert.That(where.RequiresAllowFiltering, Is.False);
    }

    [Test]
    public void Translates_partition_equality_and_clustering_range()
    {
        var date = new DateTime(2026, 1, 1);
        var where = Translate(x => x.TenantId == 5 && x.CreatedAt >= date);

        Assert.That(where.Cql, Is.EqualTo("TenantId = ? AND CreatedAt >= ?"));
        Assert.That(where.RequiresAllowFiltering, Is.False);
    }

    [Test]
    public void Flags_allow_filtering_for_a_non_key_column()
    {
        var where = Translate(x => x.TenantId == 5 && x.Total > 100m);

        Assert.That(where.Cql, Is.EqualTo("TenantId = ? AND Total > ?"));
        Assert.That(where.RequiresAllowFiltering, Is.True);
    }

    [Test]
    public void Flips_a_reversed_comparison()
    {
        var where = Translate(x => 100m < x.Total);

        Assert.That(where.Cql, Is.EqualTo("Total > ?"));
        Assert.That(where.RequiresAllowFiltering, Is.True);
    }

    [Test]
    public void Translates_a_captured_variable_via_partial_evaluation()
    {
        var tenant = 7;
        var where = Translate(x => x.TenantId == tenant);

        Assert.That(where.Parameters, Is.EqualTo(new object?[] { 7 }));
    }

    [Test]
    public void Rejects_a_range_on_the_partition_key()
    {
        Assert.That(() => Translate(x => x.TenantId > 5), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void Rejects_or()
    {
        Assert.That(() => Translate(x => x.TenantId == 5 || x.Total > 1m), Throws.TypeOf<NotSupportedException>());
    }
}
