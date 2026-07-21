using System.Linq.Expressions;
using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Tests;

/// <summary>
///     Unit tests for the node-model splitting surface (<see cref="NodePredicates" /> +
///     <see cref="FilterInterpreter.ToNode" />/<see cref="FilterInterpreter.RebuildPredicate{TEntity}" />): one
///     conversion pass, conjuncts/disjuncts split on the nodes, and a refused conjunct rebuilt into a compilable
///     predicate for client-side residual evaluation.
/// </summary>
[TestFixture]
public sealed class NodePredicatesTests
{
    [Test]
    public void Conjuncts_flatten_nested_ands_in_order()
    {
        var lambda = FilterInterpreter.ToNode((Expression<Func<Sample, bool>>)(x => x.TenantId == 1 && (x.Total > 2m && x.IsActive)));

        Assert.That(NodePredicates.Conjuncts(lambda.Body), Has.Count.EqualTo(3));
    }

    [Test]
    public void Conjuncts_keep_a_disjunction_whole()
    {
        var lambda = FilterInterpreter.ToNode((Expression<Func<Sample, bool>>)(x => x.TenantId == 1 || x.Total > 2m));

        Assert.That(NodePredicates.Conjuncts(lambda.Body), Has.Count.EqualTo(1));
        Assert.That(NodePredicates.Disjuncts(lambda.Body), Has.Count.EqualTo(2));
    }

    [Test]
    public void A_rebuilt_conjunct_compiles_with_its_folded_captures()
    {
        var threshold = 10m;
        var lambda = FilterInterpreter.ToNode((Expression<Func<Sample, bool>>)(x => x.TenantId == 1 && x.Total > threshold));
        var conjuncts = NodePredicates.Conjuncts(lambda.Body);

        var residual = FilterInterpreter.RebuildPredicate<Sample>(lambda, conjuncts[1]).Compile();

        Assert.That(residual(new Sample { Total = 11m }), Is.True, "the captured threshold was folded into the rebuilt predicate");
        Assert.That(residual(new Sample { Total = 9m }), Is.False);
    }

    [Test]
    public void Interpret_accepts_a_node_conjunct_directly()
    {
        var lambda = FilterInterpreter.ToNode((Expression<Func<Sample, bool>>)(x => x.TenantId == 5 && x.Name != "x"));
        var conjuncts = NodePredicates.Conjuncts(lambda.Body);

        var pushable = (ComparisonFilter)FilterInterpreter.Interpret(conjuncts[0]);

        Assert.That(pushable.Member, Is.EqualTo("TenantId"));
        Assert.That(pushable.Value, Is.EqualTo(5));
    }
}
