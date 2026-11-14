using System.Linq.Expressions;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Tests;

/// <summary>
///     A priority stored as its labels whose three orders all differ: the members' values (low, medium, high, urgent), the
///     labels' text (a_urgent, high, low, medium) and the declaration order (high, low, a_urgent, medium) a PostgreSQL enum
///     type or a MySQL ENUM would keep.
/// </summary>
[EnumType]
public enum Priority
{
    [StoredAs("high")]
    High = 3,

    [StoredAs("low")]
    Low = 1,

    [StoredAs("a_urgent")]
    Urgent = 4,

    [StoredAs("medium")]
    Medium = 2,
}

/// <summary>A state whose two members share a value: an alias, which C# cannot tell from the member it aliases.</summary>
[EnumType]
public enum AliasedState
{
    [StoredAs("open")]
    Open = 0,

    [StoredAs("active")]
    Active = 0,

    [StoredAs("closed")]
    Closed = 1,
}

/// <summary>A ticket with a priority and an escalation that may be unset.</summary>
public sealed class Ticket
{
    public Priority Priority { get; set; }

    public Priority? Escalation { get; set; }
}

/// <summary>
///     Unit tests for <see cref="EnumOrder" />: an enum stored as its labels compares and orders by its members' values, as
///     C# does, whatever order the labels' text or the declaration would give.
/// </summary>
[TestFixture]
public sealed class EnumOrderTests
{
    [Test]
    public void The_ranks_follow_the_members_values()
    {
        Assert.That(EnumOrder.Ranks(typeof(Priority)).Select(rank => (rank.Key, rank.Value)),
            Is.EqualTo(new[] { ("low", 1m), ("medium", 2m), ("high", 3m), ("a_urgent", 4m) }));
        Assert.That(EnumOrder.ByValue(typeof(Priority?)), Is.EqualTo(new Enum[] { Priority.Low, Priority.Medium, Priority.High, Priority.Urgent }),
            "a nullable member's enum is the same enum");
    }

    [Test]
    public void An_enum_whose_members_share_a_value_is_not_stored_as_labels()
    {
        // Enum.GetName names a shared value by either member, unspecified, so its label would be either: a row written as
        // "open" could be filtered as "active" and not found.
        Assert.That(() => EnumLabels.Of(typeof(AliasedState)),
            Throws.InvalidOperationException.With.Message.Contains("'AliasedState.Open' and 'AliasedState.Active' share the value 0"));
        Assert.That(() => EnumLabels.Label(AliasedState.Closed), Throws.InvalidOperationException, "nor is any of its members written");
    }

    [TestCase(ComparisonOperator.GreaterThanOrEqual, new[] { Priority.Medium, Priority.High, Priority.Urgent })]
    [TestCase(ComparisonOperator.GreaterThan, new[] { Priority.High, Priority.Urgent })]
    [TestCase(ComparisonOperator.LessThan, new[] { Priority.Low })]
    [TestCase(ComparisonOperator.LessThanOrEqual, new[] { Priority.Low, Priority.Medium })]
    public void An_ordered_comparison_selects_the_members_whose_value_satisfies_it(ComparisonOperator op, Priority[] expected)
    {
        Assert.That(EnumOrder.Satisfying(typeof(Priority), op, Priority.Medium), Is.EqualTo(expected));
        Assert.That(EnumOrder.Satisfying(typeof(Priority), op, 2), Is.EqualTo(expected),
            "C# compiles the comparison into one of numbers, so the value may arrive as the number");
    }

    [Test]
    public void A_comparison_no_member_satisfies_selects_none()
    {
        Assert.That(EnumOrder.Satisfying(typeof(Priority), ComparisonOperator.GreaterThan, Priority.Urgent), Is.Empty);
    }

    [Test]
    public void An_ordered_comparison_of_a_member_stored_as_labels_becomes_membership()
    {
        Expression<Func<Ticket, bool>> predicate = x => x.Priority >= Priority.Medium && !(x.Escalation < Priority.High);

        var rewritten = EnumOrder.RewriteComparisons(predicate, type => type == typeof(Priority));

        Assert.That(rewritten.ToString(), Does.Not.Contain(">=").And.Not.Contain("<"), rewritten.ToString());
        var run = rewritten.Compile();
        foreach (var priority in Enum.GetValues<Priority>())
        {
            foreach (var escalation in Enum.GetValues<Priority>().Cast<Priority?>().Append(null))
            {
                var ticket = new Ticket { Priority = priority, Escalation = escalation };
                Assert.That(run(ticket), Is.EqualTo(predicate.Compile()(ticket)), $"{priority}, {escalation?.ToString() ?? "null"}");
            }
        }
    }

    [Test]
    public void A_member_whose_enum_is_stored_as_its_number_is_left_alone()
    {
        Expression<Func<Ticket, bool>> predicate = x => x.Priority >= Priority.Medium;

        Assert.That(EnumOrder.RewriteComparisons(predicate, _ => false), Is.SameAs(predicate));
    }
    [Test]
    public void A_selector_of_a_member_stored_as_labels_names_its_enum_converted_or_not()
    {
        Assert.That(EnumOrder.LabelEnumOf((Expression<Func<Ticket, Priority>>)(x => x.Priority), type => type == typeof(Priority)),
            Is.EqualTo(typeof(Priority)));
        Assert.That(EnumOrder.LabelEnumOf((Expression<Func<Ticket, int>>)(x => (int)x.Priority), type => type == typeof(Priority)),
            Is.EqualTo(typeof(Priority)));
        Assert.That(EnumOrder.LabelEnumOf((Expression<Func<Ticket, Priority?>>)(x => x.Escalation), type => type == typeof(Priority)),
            Is.EqualTo(typeof(Priority)));
        Assert.That(EnumOrder.LabelEnumOf((Expression<Func<Ticket, Priority>>)(x => x.Priority), _ => false), Is.Null);
    }

    [Test]
    public void A_minimum_or_a_maximum_answers_the_member_or_its_number_by_value()
    {
        object?[] present = [Priority.High, null, Priority.Low, Priority.Urgent];

        Assert.That(EnumOrder.FromValue<Priority>(typeof(Priority), EnumOrder.Extreme(present, largest: false)), Is.EqualTo(Priority.Low));
        Assert.That(EnumOrder.FromValue<Priority?>(typeof(Priority), EnumOrder.Extreme(present, largest: true)), Is.EqualTo(Priority.Urgent));
        Assert.That(EnumOrder.FromValue<int>(typeof(Priority), EnumOrder.Extreme(present, largest: true)), Is.EqualTo(4));
        Assert.That(EnumOrder.FromValue<Priority?>(typeof(Priority), EnumOrder.Extreme([null], largest: true)), Is.Null);
    }

    [Test]
    public void A_group_aggregating_a_member_stored_as_labels_is_refused()
    {
        var group = GroupInterpreter.Interpret<Ticket, Priority, object>(x => x.Priority, g => new { g.Key, Top = g.Max(x => x.Escalation) });

        Assert.That(() => EnumOrder.RefuseLabelAggregates(group, null, member => member == "Escalation" ? typeof(Priority) : null),
            Throws.InstanceOf<NotSupportedException>().With.Message.Contains("labels"));
        Assert.That(() => EnumOrder.RefuseLabelAggregates(group, null, _ => null), Throws.Nothing);
    }
}
