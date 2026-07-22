using System.Linq.Expressions;
using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Tests;

/// <summary>
///     Unit tests for the reusable <see cref="GroupInterpreter" /> — pure expression analysis, no store. Proves the
///     typed <c>GroupBy</c> surface accepts exactly the shapes a store can aggregate server-side, and rejects the
///     rest with the supported shapes.
/// </summary>
[TestFixture]
public sealed class GroupInterpreterTests
{
    private static GroupQuery Interpret<TKey, TResult>(
        Expression<Func<Sample, TKey>> keySelector,
        Expression<Func<IGrouping<TKey, Sample>, TResult>> resultSelector) =>
        GroupInterpreter.Interpret(keySelector, resultSelector);

    [Test]
    public void Single_key_with_count_sum_and_average()
    {
        var query = Interpret(x => x.Name,
            g => new { Customer = g.Key, Orders = g.Count(), Total = g.Sum(x => x.Total), Mean = g.Average(x => x.TenantId) });

        Assert.That(query.Key.Single().Path, Is.EqualTo("Name"));
        Assert.That(query.Key.Single().Name, Is.Null);
        Assert.That(query.ConstructorProjection, Is.True);
        Assert.That(query.Bindings, Has.Count.EqualTo(4));
        Assert.That(((GroupKeyBinding)query.Bindings[0]).KeyName, Is.Null);
        Assert.That(((GroupAggregateBinding)query.Bindings[1]).Aggregate, Is.EqualTo(GroupAggregate.Count));
        var sum = (GroupAggregateBinding)query.Bindings[2];
        Assert.That((sum.Aggregate, sum.Member), Is.EqualTo((GroupAggregate.Sum, "Total")));
        Assert.That(((GroupAggregateBinding)query.Bindings[3]).Aggregate, Is.EqualTo(GroupAggregate.Average));
    }

    [Test]
    public void Composite_key_members_project_individually()
    {
        var query = Interpret(x => new { x.Name, x.TenantId },
            g => new { g.Key.Name, g.Key.TenantId, Latest = g.Max(x => x.CreatedAt) });

        Assert.That(query.Key.Select(member => (member.Path, member.Name)),
            Is.EqualTo(new[] { ("Name", "Name"), ("TenantId", "TenantId") }));
        Assert.That(((GroupKeyBinding)query.Bindings[0]).KeyName, Is.EqualTo("Name"));
        Assert.That(((GroupKeyBinding)query.Bindings[1]).KeyName, Is.EqualTo("TenantId"));
        var max = (GroupAggregateBinding)query.Bindings[2];
        Assert.That((max.Aggregate, max.Member), Is.EqualTo((GroupAggregate.Max, "CreatedAt")));
    }

    [Test]
    public void Whole_composite_key_projects_as_the_key()
    {
        var query = Interpret(x => new { x.Name, x.IsActive }, g => new { Bucket = g.Key, Rows = g.LongCount() });

        Assert.That(((GroupKeyBinding)query.Bindings[0]).KeyName, Is.Null);
        Assert.That(((GroupAggregateBinding)query.Bindings[1]).Aggregate, Is.EqualTo(GroupAggregate.Count));
    }

    [Test]
    public void Member_init_projection_binds_by_member()
    {
        var query = Interpret(x => x.TenantId,
            g => new TenantSummary { Tenant = g.Key, Orders = g.Count(), Smallest = g.Min(x => x.Total) });

        Assert.That(query.ConstructorProjection, Is.False);
        Assert.That(query.Bindings.Select(binding => binding.Target), Is.EqualTo(new[] { "Tenant", "Orders", "Smallest" }));
        var min = (GroupAggregateBinding)query.Bindings[2];
        Assert.That((min.Aggregate, min.Member), Is.EqualTo((GroupAggregate.Min, "Total")));
    }

    [Test]
    public void Unsupported_shapes_are_rejected_with_the_supported_ones()
    {
        Assert.That(() => Interpret(x => x.TenantId + 1, g => new { g.Key }),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("Supported shapes"));
        Assert.That(() => Interpret(x => x.Name, g => new { Weird = g.Count() * 2 }),
            Throws.TypeOf<NotSupportedException>());
        Assert.That(() => Interpret(x => x.Name, g => new { First = g.Select(x => x.Total).First() }),
            Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void Projecting_a_member_the_key_does_not_have_is_rejected()
    {
        Assert.That(() => Interpret(x => new { x.Name, x.TenantId }, g => new { g.Key.Name, Rows = g.Count() }),
            Throws.Nothing);
        Assert.That(() => Interpret(x => x.Name, g => new { Only = g.Key.Length }),
            Throws.TypeOf<NotSupportedException>(), "a single key has no projectable members");
    }

    private sealed class TenantSummary
    {
        public int Tenant { get; set; }

        public int Orders { get; set; }

        public decimal Smallest { get; set; }
    }
}
