using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Tests;

/// <summary>
///     Unit tests for <see cref="MemberSelection" /> (what an upsert's target and a member-scoped modify name) and
///     <see cref="UpdateApplication" /> (an update's value computed from the row, for a store that writes it under a
///     compare-and-set) — pure expression work, no store.
/// </summary>
[TestFixture]
public sealed class MemberSelectionTests
{
    [Test]
    public void One_member_or_several_in_an_anonymous_type_are_named_in_order()
    {
        Assert.That(MemberSelection.Names((System.Linq.Expressions.Expression<Func<Sample, object?>>)(x => x.Name)),
            Is.EqualTo(new[] { "Name" }));
        Assert.That(MemberSelection.Names((System.Linq.Expressions.Expression<Func<Sample, object?>>)(x => x.TenantId)),
            Is.EqualTo(new[] { "TenantId" }), "a value-typed member arrives boxed, and is still the member");
        Assert.That(MemberSelection.Names((System.Linq.Expressions.Expression<Func<Sample, object?>>)(x => new { x.TenantId, x.Name })),
            Is.EqualTo(new[] { "TenantId", "Name" }));
    }

    [Test]
    public void Anything_but_a_member_read_off_the_entity_is_refused()
    {
        Assert.That(() => MemberSelection.Names((System.Linq.Expressions.Expression<Func<Sample, object?>>)(x => x.Name.ToUpper())),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("does not name members"));
        Assert.That(() => MemberSelection.Names((System.Linq.Expressions.Expression<Func<Sample, object?>>)(x => new { Sum = x.TenantId + 1 })),
            Throws.TypeOf<NotSupportedException>());
    }

    [Test]
    public void An_update_applies_in_memory_as_the_store_applies_it()
    {
        var row = new Sample { TenantId = 7, Name = "a", Total = 2.5m, Tags = ["x", "y"] };
        foreach (var assignment in UpdateInterpreter.Interpret<Sample>(x => new Sample
                 {
                     TenantId = x.TenantId + 1,
                     Name = "b",
                     Total = x.Total * 2,
                     Tags = x.Tags.Append("z").ToList(),
                 }))
        {
            UpdateApplication.Apply(row, assignment);
        }

        Assert.That(row.TenantId, Is.EqualTo(8));
        Assert.That(row.Name, Is.EqualTo("b"));
        Assert.That(row.Total, Is.EqualTo(5m));
        Assert.That(row.Tags, Is.EqualTo(new[] { "x", "y", "z" }));

        foreach (var assignment in UpdateInterpreter.Interpret<Sample>(x => new Sample { Tags = x.Tags.Except(new[] { "x" }).ToList() }))
        {
            UpdateApplication.Apply(row, assignment);
        }

        Assert.That(row.Tags, Is.EqualTo(new[] { "y", "z" }));
    }
}
