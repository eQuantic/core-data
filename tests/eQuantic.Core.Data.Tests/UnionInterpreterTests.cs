using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Tests;

/// <summary>
///     Unit tests for the union composer and the reusable <see cref="UnionInterpreter" /> — pure expression
///     analysis, no store. Proves branch projections accept exactly the shapes every store can place in a
///     combined select (members and constants), and that the composer validates the query surface.
/// </summary>
[TestFixture]
public sealed class UnionInterpreterTests
{
    [Test]
    public void Members_and_constants_bind_in_projection_order()
    {
        var branch = Union.Of<Sample>().Where(x => x.IsActive)
            .Select(x => new { x.Name, Origin = "sample", Rank = 3 });
        var projection = UnionInterpreter.Interpret(branch);

        Assert.That(branch.EntityType, Is.EqualTo(typeof(Sample)));
        Assert.That(branch.Filters, Has.Count.EqualTo(1));
        Assert.That(projection.ConstructorProjection, Is.True);
        Assert.That(projection.Bindings.Select(binding => binding.Target), Is.EqualTo(new[] { "Name", "Origin", "Rank" }));
        Assert.That(((UnionColumnBinding)projection.Bindings[0]).Member, Is.EqualTo("Name"));
        Assert.That(((UnionConstantBinding)projection.Bindings[1]).Value, Is.EqualTo("sample"));
        Assert.That(((UnionConstantBinding)projection.Bindings[2]).Value, Is.EqualTo(3));
    }

    [Test]
    public void Captured_values_fold_into_constants()
    {
        var origin = "captured";
        var projection = UnionInterpreter.Interpret(
            Union.Of<Sample>().Select(x => new { x.TenantId, Origin = origin }));

        Assert.That(((UnionConstantBinding)projection.Bindings[1]).Value, Is.EqualTo("captured"));
    }

    [Test]
    public void Member_init_projection_binds_by_member()
    {
        var projection = UnionInterpreter.Interpret(
            Union.Of<Sample>().Select(x => new Row { Name = x.Name, Origin = "s" }));

        Assert.That(projection.ConstructorProjection, Is.False);
        Assert.That(projection.Bindings.Select(binding => binding.Target), Is.EqualTo(new[] { "Name", "Origin" }));
    }

    [Test]
    public void Computed_members_are_rejected_with_guidance()
    {
        Assert.That(() => UnionInterpreter.Interpret(Union.Of<Sample>().Select(x => new { Doubled = x.TenantId * 2 })),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("entity member or a constant"));
    }

    [Test]
    public void The_composer_validates_branch_count_and_paging()
    {
        var branch = Union.Of<Sample>().Select(x => new { x.Name });

        Assert.That(() => UnionQuery.All(branch), Throws.ArgumentException);
        Assert.That(() => UnionQuery.All(branch, Union.Of<Sample>().Select(x => new { x.Name })).Take(0),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Order_members_resolve_from_the_result_shape()
    {
        var query = UnionQuery.Distinct(
                Union.Of<Sample>().Select(x => new { x.Name }),
                Union.Of<Sample>().IgnoringQueryFilters().Select(x => new { x.Name }))
            .OrderByDescending(row => row.Name).Take(5).Skip(2);

        Assert.That(query.All, Is.False);
        Assert.That(query.Branches[1].IgnoreQueryFilters, Is.True);
        Assert.That(query.Order.Single(), Is.EqualTo(new UnionOrder("Name", Descending: true)));
        Assert.That((query.Limit, query.Offset), Is.EqualTo(((int?)5, (int?)2)));
    }

    private sealed class Row
    {
        public string Name { get; set; } = "";

        public string Origin { get; set; } = "";
    }
}
