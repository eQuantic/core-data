using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Tests;

/// <summary>
///     Unit tests for the reusable <see cref="FilterInterpreter" /> — pure expression analysis, no store. Proves it
///     produces the right dialect-agnostic filter model for every shape it claims to support.
/// </summary>
[TestFixture]
public sealed class FilterInterpreterTests
{
    [Test]
    public void Equality_becomes_a_comparison()
    {
        var filter = (ComparisonFilter)FilterInterpreter.Interpret<Sample>(x => x.TenantId == 5);

        Assert.That(filter.Member, Is.EqualTo("TenantId"));
        Assert.That(filter.Operator, Is.EqualTo(ComparisonOperator.Equal));
        Assert.That(filter.Value, Is.EqualTo(5));
    }

    [Test]
    public void Range_becomes_a_comparison()
    {
        var filter = (ComparisonFilter)FilterInterpreter.Interpret<Sample>(x => x.Total > 100m);

        Assert.That(filter.Operator, Is.EqualTo(ComparisonOperator.GreaterThan));
        Assert.That(filter.Value, Is.EqualTo(100m));
    }

    [Test]
    public void Reversed_comparison_is_flipped()
    {
        var filter = (ComparisonFilter)FilterInterpreter.Interpret<Sample>(x => 100m < x.Total);

        Assert.That(filter.Member, Is.EqualTo("Total"));
        Assert.That(filter.Operator, Is.EqualTo(ComparisonOperator.GreaterThan));
    }

    [Test]
    public void Not_equal_becomes_a_comparison()
    {
        var filter = (ComparisonFilter)FilterInterpreter.Interpret<Sample>(x => x.Name != "x");
        Assert.That(filter.Operator, Is.EqualTo(ComparisonOperator.NotEqual));
    }

    [Test]
    public void And_becomes_a_logical_conjunction()
    {
        var filter = (LogicalFilter)FilterInterpreter.Interpret<Sample>(x => x.TenantId == 5 && x.Total > 1m);

        Assert.That(filter.Operator, Is.EqualTo(LogicalOperator.And));
        Assert.That(filter.Operands, Has.Count.EqualTo(2));
    }

    [Test]
    public void Or_over_different_members_stays_a_disjunction()
    {
        var filter = (LogicalFilter)FilterInterpreter.Interpret<Sample>(x => x.TenantId == 5 || x.Total > 1m);
        Assert.That(filter.Operator, Is.EqualTo(LogicalOperator.Or));
    }

    [Test]
    public void Or_of_equalities_on_one_member_folds_into_in()
    {
        var filter = (InFilter)FilterInterpreter.Interpret<Sample>(x => x.TenantId == 1 || x.TenantId == 2 || x.TenantId == 3);

        Assert.That(filter.Member, Is.EqualTo("TenantId"));
        Assert.That(filter.Values, Is.EqualTo(new object?[] { 1, 2, 3 }));
    }

    [Test]
    public void Array_contains_member_becomes_in()
    {
        var ids = new[] { 1, 2, 3 };
        var filter = (InFilter)FilterInterpreter.Interpret<Sample>(x => ids.Contains(x.TenantId));

        Assert.That(filter.Member, Is.EqualTo("TenantId"));
        Assert.That(filter.Values, Is.EqualTo(new object?[] { 1, 2, 3 }));
    }

    [Test]
    public void List_contains_member_becomes_in()
    {
        var names = new List<string> { "a", "b" };
        var filter = (InFilter)FilterInterpreter.Interpret<Sample>(x => names.Contains(x.Name));

        Assert.That(filter.Member, Is.EqualTo("Name"));
        Assert.That(filter.Values, Is.EqualTo(new object?[] { "a", "b" }));
    }

    [Test]
    public void Member_collection_contains_value_becomes_contains()
    {
        var filter = (CollectionFilter)FilterInterpreter.Interpret<Sample>(x => x.Tags.Contains("vip"));

        Assert.That(filter.Member, Is.EqualTo("Tags"));
        Assert.That(filter.Value, Is.EqualTo("vip"));
        Assert.That(filter.Key, Is.False);
    }

    [Test]
    public void Member_map_contains_key_becomes_contains_key()
    {
        var filter = (CollectionFilter)FilterInterpreter.Interpret<Sample>(x => x.Attributes.ContainsKey("tier"));

        Assert.That(filter.Member, Is.EqualTo("Attributes"));
        Assert.That(filter.Value, Is.EqualTo("tier"));
        Assert.That(filter.Key, Is.True);
    }

    [Test]
    public void Boolean_member_becomes_equality_to_true()
    {
        var filter = (ComparisonFilter)FilterInterpreter.Interpret<Sample>(x => x.IsActive);

        Assert.That(filter.Member, Is.EqualTo("IsActive"));
        Assert.That(filter.Value, Is.EqualTo(true));
    }

    [Test]
    public void Negated_boolean_member_becomes_equality_to_false()
    {
        var filter = (ComparisonFilter)FilterInterpreter.Interpret<Sample>(x => !x.IsActive);
        Assert.That(filter.Value, Is.EqualTo(false));
    }

    [Test]
    public void Negated_comparison_is_pushed_into_the_operator()
    {
        var filter = (ComparisonFilter)FilterInterpreter.Interpret<Sample>(x => !(x.Total > 100m));
        Assert.That(filter.Operator, Is.EqualTo(ComparisonOperator.LessThanOrEqual));
    }

    [Test]
    public void Captured_variable_is_partial_evaluated()
    {
        var tenant = 7;
        var filter = (ComparisonFilter)FilterInterpreter.Interpret<Sample>(x => x.TenantId == tenant);
        Assert.That(filter.Value, Is.EqualTo(7));
    }

    [Test]
    public void Inline_constructed_value_is_evaluated_at_translation_time()
    {
        var filter = (ComparisonFilter)FilterInterpreter.Interpret<Sample>(x => x.CreatedAt >= new DateTime(2026, 1, 1));

        Assert.That(filter.Member, Is.EqualTo("CreatedAt"));
        Assert.That(filter.Value, Is.EqualTo(new DateTime(2026, 1, 1)));
    }

    [Test]
    public void Static_member_value_is_evaluated_at_translation_time()
    {
        var filter = (ComparisonFilter)FilterInterpreter.Interpret<Sample>(x => x.Name == string.Empty);
        Assert.That(filter.Value, Is.EqualTo(""));
    }

    [Test]
    public void CompareTo_against_zero_normalizes_to_the_direct_comparison()
    {
        var filter = (ComparisonFilter)FilterInterpreter.Interpret<Sample>(x => x.Name.CompareTo("m") > 0);

        Assert.That(filter.Member, Is.EqualTo("Name"));
        Assert.That(filter.Operator, Is.EqualTo(ComparisonOperator.GreaterThan));
        Assert.That(filter.Value, Is.EqualTo("m"));
    }

    [Test]
    public void Parameter_referencing_operand_stays_unsupported()
    {
        Assert.That(() => FilterInterpreter.Interpret<Sample>(x => x.TenantId == x.Name.Length),
            Throws.TypeOf<NotSupportedException>());
    }
}
