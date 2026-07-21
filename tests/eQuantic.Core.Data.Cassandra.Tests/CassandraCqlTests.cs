using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>
///     Unit tests for <see cref="CassandraCql" /> — the options-to-WHERE bridge — pure, no Cassandra. Proves the
///     options' filters and an extra predicate (a <c>GetFiltered</c> argument or an id lookup) compose into one
///     conjunction without mutating the caller's options, and that the <c>ALLOW FILTERING</c> opt-in rides its own
///     channel instead of the diagnostic tag.
/// </summary>
[TestFixture]
public sealed class CassandraCqlTests
{
    private static CassandraEntityConfiguration Config() =>
        new CassandraModelBuilder()
            .Entity<OrderData>(entity => entity
                .Table("orders")
                .PartitionKey(x => x.TenantId)
                .ClusteringKey(x => x.CreatedAt, descending: true))
            .Build()
            .For(typeof(OrderData));

    [Test]
    public void Options_filter_and_extra_filter_compose_with_and()
    {
        var date = new DateTime(2026, 1, 1);
        var options = new QueryOptions<OrderData>().Where(x => x.TenantId == 5);

        var (where, values, filtering) = CassandraCql.Where(Config(), options, x => x.CreatedAt >= date);

        Assert.That(where, Is.EqualTo("TenantId = ? AND CreatedAt >= ?"));
        Assert.That(values, Is.EqualTo(new object?[] { 5, date }));
        Assert.That(filtering, Is.False);
    }

    [Test]
    public void The_callers_options_are_not_mutated()
    {
        var options = new QueryOptions<OrderData>();

        _ = CassandraCql.Where(Config(), options, x => x.TenantId == 1);

        Assert.That(options.Filter, Is.Null);
    }

    [Test]
    public void An_extra_filter_alone_renders_without_options()
    {
        var (where, values, filtering) = CassandraCql.Where<OrderData>(Config(), null, x => x.TenantId == 3);

        Assert.That(where, Is.EqualTo("TenantId = ?"));
        Assert.That(values, Is.EqualTo(new object?[] { 3 }));
        Assert.That(filtering, Is.False);
    }

    [Test]
    public void Allow_filtering_leaves_the_diagnostic_tag_free()
    {
        var options = new QueryOptions<OrderData>().WithTag("audit").AllowFiltering();

        Assert.That(options.Tag, Is.EqualTo("audit"));
        Assert.That(CassandraCql.AllowFilteringOptedIn(options), Is.True);
        Assert.That(CassandraCql.AllowFilteringOptedIn(new QueryOptions<OrderData>()), Is.False);
    }

    // ---------------------------------------------------------------- pushdown plans (residual engine)

    [Test]
    public void Plan_pushes_the_expressible_conjunct_and_keeps_the_rest_residual()
    {
        var plan = CassandraCql.Plan<OrderData>(Config(), null, x => x.TenantId == 5 && x.Status != "closed");

        Assert.That(plan.Where, Is.EqualTo("TenantId = ?"));
        Assert.That(plan.Values, Is.EqualTo(new object?[] { 5 }));
        Assert.That(plan.Residual, Has.Count.EqualTo(1));
        Assert.That(plan.ResidualText, Does.Contain("Status"));
        Assert.That(plan.PartitionScoped, Is.True);
        Assert.That(plan.RequiresAllowFiltering, Is.False);
    }

    [Test]
    public void Plan_sends_an_or_across_columns_to_residual()
    {
        var plan = CassandraCql.Plan<OrderData>(Config(), null, x => x.TenantId == 1 || x.Total > 1m);

        Assert.That(plan.Where, Is.Empty);
        Assert.That(plan.Residual, Has.Count.EqualTo(1));
        Assert.That(plan.PartitionScoped, Is.False);
    }

    [Test]
    public void Plan_sends_a_null_comparison_to_residual()
    {
        var plan = CassandraCql.Plan<OrderData>(Config(), null, x => x.TenantId == 5 && x.Status == null!);

        Assert.That(plan.Where, Is.EqualTo("TenantId = ?"));
        Assert.That(plan.Residual, Has.Count.EqualTo(1));
    }

    [Test]
    public void Plan_sends_an_arbitrary_predicate_to_residual()
    {
        var plan = CassandraCql.Plan<OrderData>(Config(), null, x => x.Status.StartsWith("cl"));

        Assert.That(plan.Where, Is.Empty);
        Assert.That(plan.Residual, Has.Count.EqualTo(1));
    }

    [Test]
    public void Plan_with_a_fully_expressible_filter_has_no_residual()
    {
        var date = new DateTime(2026, 1, 1);
        var plan = CassandraCql.Plan<OrderData>(Config(), null, x => x.TenantId == 5 && x.CreatedAt >= date);

        Assert.That(plan.Where, Is.EqualTo("TenantId = ? AND CreatedAt >= ?"));
        Assert.That(plan.Residual, Is.Empty);
        Assert.That(plan.PartitionScoped, Is.True);
    }

    [Test]
    public void Plan_folds_an_inline_constructed_value_into_the_pushdown()
    {
        // `new DateTime(...)` inline stays structural in the node model; the interpreter evaluates the
        // parameter-free subtree at translation time, so the clause pushes down like a captured variable.
        var plan = CassandraCql.Plan<OrderData>(Config(), null, x => x.TenantId == 5 && x.CreatedAt >= new DateTime(2026, 1, 1));

        Assert.That(plan.Where, Is.EqualTo("TenantId = ? AND CreatedAt >= ?"));
        Assert.That(plan.Values, Is.EqualTo(new object?[] { 5, new DateTime(2026, 1, 1) }));
        Assert.That(plan.Residual, Is.Empty);
    }
}
