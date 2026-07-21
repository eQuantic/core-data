using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Tests;

/// <summary>
///     Unit tests for the reusable <see cref="UpdateInterpreter" /> — pure expression analysis, no store. Proves
///     each supported computed shape produces the right dialect-agnostic assignment, and that shapes no store can
///     apply atomically are rejected.
/// </summary>
[TestFixture]
public sealed class UpdateInterpreterTests
{
    [Test]
    public void Constant_assignment_becomes_set()
    {
        var assignment = (SetAssignment)Single(x => new Sample { Name = "closed" });

        Assert.That(assignment.Name, Is.EqualTo("Name"));
        Assert.That(assignment.Value, Is.EqualTo("closed"));
    }

    [Test]
    public void Captured_variable_assignment_becomes_set()
    {
        var status = "open";
        var assignment = (SetAssignment)Single(x => new Sample { Name = status });

        Assert.That(assignment.Value, Is.EqualTo("open"));
    }

    [Test]
    public void Member_plus_constant_becomes_increment()
    {
        var assignment = (IncrementAssignment)Single(x => new Sample { TenantId = x.TenantId + 5 });

        Assert.That(assignment.Name, Is.EqualTo("TenantId"));
        Assert.That(assignment.Delta, Is.EqualTo(5));
    }

    [Test]
    public void Constant_plus_member_becomes_increment()
    {
        var assignment = (IncrementAssignment)Single(x => new Sample { TenantId = 5 + x.TenantId });

        Assert.That(assignment.Delta, Is.EqualTo(5));
    }

    [Test]
    public void Member_minus_constant_becomes_a_negative_increment()
    {
        var assignment = (IncrementAssignment)Single(x => new Sample { TenantId = x.TenantId - 3 });

        Assert.That(assignment.Delta, Is.EqualTo(-3));
    }

    [Test]
    public void Member_times_constant_becomes_multiply()
    {
        var assignment = (MultiplyAssignment)Single(x => new Sample { Total = x.Total * 2m });

        Assert.That(assignment.Name, Is.EqualTo("Total"));
        Assert.That(assignment.Factor, Is.EqualTo(2m));
    }

    [Test]
    public void Append_becomes_a_collection_add()
    {
        var assignment = (CollectionAddAssignment)Single(x => new Sample { Tags = x.Tags.Append("vip").ToList() });

        Assert.That(assignment.Name, Is.EqualTo("Tags"));
        Assert.That(assignment.Items, Is.EqualTo(new object?[] { "vip" }));
        Assert.That(assignment.Prepend, Is.False);
        Assert.That(assignment.Unique, Is.False);
    }

    [Test]
    public void Concat_with_the_member_first_appends()
    {
        var more = new[] { "a", "b" };
        var assignment = (CollectionAddAssignment)Single(x => new Sample { Tags = x.Tags.Concat(more).ToList() });

        Assert.That(assignment.Items, Is.EqualTo(new object?[] { "a", "b" }));
        Assert.That(assignment.Prepend, Is.False);
    }

    [Test]
    public void Concat_with_the_member_second_prepends()
    {
        var first = new[] { "a" };
        var assignment = (CollectionAddAssignment)Single(x => new Sample { Tags = first.Concat(x.Tags).ToList() });

        Assert.That(assignment.Prepend, Is.True);
    }

    [Test]
    public void Union_becomes_a_unique_collection_add()
    {
        var more = new[] { "vip" };
        var assignment = (CollectionAddAssignment)Single(x => new Sample { Tags = x.Tags.Union(more).ToList() });

        Assert.That(assignment.Unique, Is.True);
    }

    [Test]
    public void Except_becomes_a_collection_remove()
    {
        var gone = new[] { "old" };
        var assignment = (CollectionRemoveAssignment)Single(x => new Sample { Tags = x.Tags.Except(gone).ToList() });

        Assert.That(assignment.Items, Is.EqualTo(new object?[] { "old" }));
    }

    [Test]
    public void Typed_collection_takes_the_members_shape()
    {
        var assignment = (CollectionAddAssignment)Single(x => new Sample { Tags = x.Tags.Append("vip").ToList() });

        Assert.That(assignment.ToTypedCollection(), Is.InstanceOf<List<string>>());
    }

    [Test]
    public void String_concatenation_is_rejected()
    {
        Assert.That(() => Single(x => new Sample { Name = x.Name + "!" }),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("Name"));
    }

    [Test]
    public void Cross_member_arithmetic_is_rejected()
    {
        Assert.That(() => Single(x => new Sample { TenantId = x.TenantId * x.TenantId }),
            Throws.TypeOf<NotSupportedException>());
    }

    private static UpdateAssignment Single(System.Linq.Expressions.Expression<Func<Sample, Sample>> updateFactory) =>
        UpdateInterpreter.Interpret(updateFactory).Single();
}
