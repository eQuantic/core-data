using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Tests;

/// <summary>
///     Unit tests for <see cref="PredicateConjunctions" /> — the AND-distribution rule the pushdown engine builds
///     on: conjunctions split (each conjunct can run on a different side), disjunctions stay whole.
/// </summary>
[TestFixture]
public sealed class PredicateConjunctionsTests
{
    [Test]
    public void Split_flattens_nested_conjunctions_in_order()
    {
        var conjuncts = PredicateConjunctions.Split<Sample>(x => x.TenantId == 1 && (x.Total > 2m && x.IsActive));

        Assert.That(conjuncts, Has.Count.EqualTo(3));
        Assert.That(conjuncts[0].Body.ToString(), Does.Contain("TenantId"));
        Assert.That(conjuncts[1].Body.ToString(), Does.Contain("Total"));
        Assert.That(conjuncts[2].Body.ToString(), Does.Contain("IsActive"));
    }

    [Test]
    public void Split_keeps_a_disjunction_whole()
    {
        var conjuncts = PredicateConjunctions.Split<Sample>(x => x.TenantId == 1 || x.Total > 2m);

        Assert.That(conjuncts, Has.Count.EqualTo(1));
    }

    [Test]
    public void Split_returns_a_single_clause_as_is()
    {
        var conjuncts = PredicateConjunctions.Split<Sample>(x => x.TenantId == 1);

        Assert.That(conjuncts, Has.Count.EqualTo(1));
    }

    [Test]
    public void Split_conjuncts_share_the_original_parameter_and_compile()
    {
        var conjuncts = PredicateConjunctions.Split<Sample>(x => x.TenantId == 1 && x.IsActive);
        var sample = new Sample { TenantId = 1, IsActive = true };

        Assert.That(conjuncts.All(conjunct => conjunct.Compile()(sample)), Is.True);
    }
}
