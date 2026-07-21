using System.Linq.Expressions;
using eQuantic.Core.Data.Cassandra;
using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     Unit tests for the full Cassandra filter path (core <see cref="FilterInterpreter" /> → provider
///     <c>CassandraCqlRenderer</c>) — pure, no Cassandra. Proves the hybrid policy and the advanced constructs:
///     native keys, <c>token()</c> partition ranges, <c>IN</c>, <c>CONTAINS</c>, and <c>ALLOW FILTERING</c> flagging.
/// </summary>
[TestFixture]
public sealed class CassandraCqlRendererTests
{
    private static CassandraEntityConfiguration Config() =>
        new CassandraModelBuilder()
            .Entity<OrderData>(entity => entity
                .Table("orders")
                .PartitionKey(x => x.TenantId)
                .ClusteringKey(x => x.CreatedAt, descending: true))
            .Build()
            .For(typeof(OrderData));

    private static (string Cql, object?[] Values, bool RequiresAllowFiltering) Render(Expression<Func<OrderData, bool>> filter) =>
        CassandraCqlRenderer.Render(Config(), FilterInterpreter.Interpret(filter));

    [Test]
    public void Partition_key_equality_is_native()
    {
        var (cql, values, filtering) = Render(x => x.TenantId == 5);

        Assert.That(cql, Is.EqualTo("TenantId = ?"));
        Assert.That(values, Is.EqualTo(new object?[] { 5 }));
        Assert.That(filtering, Is.False);
    }

    [Test]
    public void Partition_equality_and_clustering_range_are_native()
    {
        var date = new DateTime(2026, 1, 1);
        var (cql, _, filtering) = Render(x => x.TenantId == 5 && x.CreatedAt >= date);

        Assert.That(cql, Is.EqualTo("TenantId = ? AND CreatedAt >= ?"));
        Assert.That(filtering, Is.False);
    }

    [Test]
    public void Range_on_the_partition_key_uses_token()
    {
        var (cql, values, filtering) = Render(x => x.TenantId > 5);

        Assert.That(cql, Is.EqualTo("token(TenantId) > token(?)"));
        Assert.That(values, Is.EqualTo(new object?[] { 5 }));
        Assert.That(filtering, Is.False);
    }

    [Test]
    public void Contains_over_a_key_renders_in()
    {
        var ids = new[] { 1, 2, 3 };
        var (cql, values, filtering) = Render(x => ids.Contains(x.TenantId));

        Assert.That(cql, Is.EqualTo("TenantId IN (?, ?, ?)"));
        Assert.That(values, Is.EqualTo(new object?[] { 1, 2, 3 }));
        Assert.That(filtering, Is.False);
    }

    [Test]
    public void Or_of_equalities_on_a_key_folds_into_in()
    {
        var (cql, _, filtering) = Render(x => x.TenantId == 1 || x.TenantId == 2);

        Assert.That(cql, Is.EqualTo("TenantId IN (?, ?)"));
        Assert.That(filtering, Is.False);
    }

    [Test]
    public void Collection_contains_needs_allow_filtering()
    {
        var (cql, values, filtering) = Render(x => x.Tags.Contains("vip"));

        Assert.That(cql, Is.EqualTo("Tags CONTAINS ?"));
        Assert.That(values, Is.EqualTo(new object?[] { "vip" }));
        Assert.That(filtering, Is.True);
    }

    [Test]
    public void Non_key_predicate_needs_allow_filtering()
    {
        var (cql, _, filtering) = Render(x => x.Total > 100m);

        Assert.That(cql, Is.EqualTo("Total > ?"));
        Assert.That(filtering, Is.True);
    }

    [Test]
    public void Boolean_member_renders_equality()
    {
        var (cql, values, _) = Render(x => x.IsPaid);

        Assert.That(cql, Is.EqualTo("IsPaid = ?"));
        Assert.That(values, Is.EqualTo(new object?[] { true }));
    }

    [Test]
    public void Or_across_different_columns_is_rejected()
    {
        Assert.That(() => Render(x => x.TenantId == 1 || x.Total > 1m), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void Not_equal_is_rejected()
    {
        Assert.That(() => Render(x => x.Status != "closed"), Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void Comparing_to_null_is_rejected()
    {
        Assert.That(() => Render(x => x.Status == null!),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("NULL"));
    }

    [Test]
    public void In_containing_null_is_rejected()
    {
        var statuses = new[] { "open", null };
        Assert.That(() => Render(x => statuses.Contains(x.Status)),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("NULL"));
    }
}
