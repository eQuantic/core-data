using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Tests;

/// <summary>An owner with a collection and a reference navigation, for the relation analysis.</summary>
public sealed class Owner
{
    public int Id { get; set; }

    public int? HolderId { get; set; }

    public Holder? Holder { get; set; }

    public List<Part>? Parts { get; set; }

    public List<string> Tags { get; set; } = [];

    public string Name { get; set; } = "";
}

/// <summary>A part of an owner.</summary>
public sealed class Part
{
    public int Id { get; set; }

    public int OwnerId { get; set; }

    public string Status { get; set; } = "";
}

/// <summary>The holder an owner references.</summary>
public sealed class Holder
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

/// <summary>
///     Unit tests for the query-predicate analysis — no store: text matched without regard to case, an array's
///     <c>Contains</c> as C# 14 binds it, and the tests a filter makes across relations.
/// </summary>
[TestFixture]
public sealed class QueryPredicateInterpretationTests
{
    private static RelationNavigation? Navigations(Type owner, PropertyInfo member) => (owner.Name, member.Name) switch
    {
        (nameof(Owner), nameof(Owner.Parts)) => new RelationNavigation(owner, member, typeof(Part), true,
            typeof(Owner).GetProperty(nameof(Owner.Id))!, typeof(Part).GetProperty(nameof(Part.OwnerId))!),
        (nameof(Owner), nameof(Owner.Holder)) => new RelationNavigation(owner, member, typeof(Holder), false,
            typeof(Owner).GetProperty(nameof(Owner.HolderId))!, typeof(Holder).GetProperty(nameof(Holder.Id))!),
        _ => null,
    };

    [TestCase(StringComparison.OrdinalIgnoreCase)]
    [TestCase(StringComparison.InvariantCultureIgnoreCase)]
    public void Equality_ignoring_case_becomes_a_text_filter(StringComparison comparison)
    {
        foreach (var predicate in new Expression<Func<Sample, bool>>[]
                 {
                     x => string.Equals(x.Name, "AcMe", comparison),
                     x => string.Equals("AcMe", x.Name, comparison),
                     x => x.Name.Equals("AcMe", comparison),
                 })
        {
            var text = (TextFilter)FilterInterpreter.Interpret(predicate);
            Assert.That((text.Member, text.Match, text.Value, text.Case), Is.EqualTo(("Name", TextMatch.Equals, "AcMe", TextCase.Ignore)));
        }
    }

    [Test]
    public void Containment_prefix_and_suffix_ignoring_case_become_text_filters()
    {
        Assert.That(((TextFilter)FilterInterpreter.Interpret<Sample>(x => x.Name.Contains("me_c", StringComparison.OrdinalIgnoreCase))).Match,
            Is.EqualTo(TextMatch.Contains));
        Assert.That(((TextFilter)FilterInterpreter.Interpret<Sample>(x => x.Name.StartsWith("a", StringComparison.OrdinalIgnoreCase))).Match,
            Is.EqualTo(TextMatch.StartsWith));
        Assert.That(((TextFilter)FilterInterpreter.Interpret<Sample>(x => x.Name.EndsWith("a", StringComparison.OrdinalIgnoreCase))).Match,
            Is.EqualTo(TextMatch.EndsWith));
        Assert.That(FilterInterpreter.Interpret<Sample>(x => x.Name.Contains("a")), Is.InstanceOf<StringFilter>(),
            "without a comparison, the store's collation decides, as before");
    }

    [Test]
    public void A_folded_member_compared_or_searched_becomes_a_text_filter()
    {
        var lower = (TextFilter)FilterInterpreter.Interpret<Sample>(x => x.Name.ToLower() == "acme");
        Assert.That((lower.Match, lower.Case, lower.CanMatch), Is.EqualTo((TextMatch.Equals, TextCase.Lower, true)));

        var upper = (TextFilter)FilterInterpreter.Interpret<Sample>(x => x.Name.ToUpperInvariant().StartsWith("AC"));
        Assert.That((upper.Match, upper.Case), Is.EqualTo((TextMatch.StartsWith, TextCase.Upper)));

        Assert.That(((TextFilter)FilterInterpreter.Interpret<Sample>(x => x.Name.ToLower() == "ACME")).CanMatch, Is.False,
            "a lower-cased text never holds an upper-case letter");
        Assert.That(FilterInterpreter.Interpret<Sample>(x => x.Name.ToLower() != "acme"), Is.InstanceOf<LogicalFilter>());
    }

    [Test]
    public void An_array_contains_bound_to_a_span_is_normalized_to_enumerable_contains()
    {
        var names = new[] { "a", "b" };
        Expression<Func<Sample, bool>> predicate = x => names.Contains(x.Name);

        var normalized = PredicateNormalizer.Normalize(predicate);
        var call = (MethodCallExpression)normalized.Body;
        Assert.That(call.Method.DeclaringType, Is.EqualTo(typeof(Enumerable)));
        Assert.That(normalized.Compile()(new Sample { Name = "b" }), Is.True, "the normalized predicate runs client-side too");

        var list = new List<string> { "a" };
        Expression<Func<Sample, bool>> listed = x => list.Contains(x.Name);
        Assert.That(PredicateNormalizer.Normalize(listed), Is.SameAs(listed), "a list's Contains is left as it is");
    }

    [Test]
    public void Relation_tests_are_found_and_normalized()
    {
        Expression<Func<Owner, bool>> predicate = x =>
            x.Parts!.Any(p => p.Status == "done") && !x.Parts!.Any() || x.Parts!.All(p => p.Status != "x")
            || x.Holder!.Name == "h" || x.Holder == null || x.Tags.Any(t => t == "embedded");

        var found = RelationPredicates.Find(predicate, Navigations);

        Assert.That(found.Select(relation => (relation.Navigation.Member.Name, relation.Negated, relation.Condition is null)), Is.EqualTo(new[]
        {
            ("Parts", false, false),
            ("Parts", false, true),
            ("Parts", true, false),
            ("Holder", false, false),
            ("Holder", true, true),
        }), "an embedded collection is not a relation");
    }

    [Test]
    public async Task Relation_tests_resolve_into_keys()
    {
        Expression<Func<Owner, bool>> predicate = x => x.Parts!.Any(p => p.Status == "done") || x.Holder == null;
        var reads = new List<(Type, string?)>();

        var resolved = await RelationPredicates.ResolveAsync(predicate, Navigations, (target, condition, member, _) =>
        {
            reads.Add((target, condition?.ToString()));
            return Task.FromResult<IReadOnlyCollection<object?>>(target == typeof(Part) ? [1, 2] : [7]);
        }, CancellationToken.None);

        Assert.That(reads, Has.Count.EqualTo(2));
        var run = resolved.Compile();
        Assert.That(run(new Owner { Id = 2, HolderId = 7 }), Is.True, "a part of owner 2 is done");
        Assert.That(run(new Owner { Id = 3, HolderId = 7 }), Is.False, "no done part, and a holder that exists");
        Assert.That(run(new Owner { Id = 3, HolderId = 8 }), Is.True, "the holder id names no holder");
        Assert.That(run(new Owner { Id = 3 }), Is.True, "no holder id at all");
    }

    [Test]
    public void A_relation_test_reading_the_owner_is_refused()
    {
        Expression<Func<Owner, bool>> correlated = x => x.Parts!.Any(p => p.OwnerId == x.Id);
        Assert.That(() => RelationPredicates.Find(correlated, Navigations),
            Throws.InstanceOf<NotSupportedException>().With.Message.Contains("entity's own members"));

        Expression<Func<Owner, bool>> counted = x => x.Parts!.Count > 0;
        Assert.That(() => RelationPredicates.Find(counted, Navigations),
            Throws.InstanceOf<NotSupportedException>().With.Message.Contains("Any() or All()"));
    }

    [Test]
    public void A_marked_relation_becomes_a_function_filter_carrying_the_relation()
    {
        Expression<Func<Owner, bool>> predicate = x => x.Name == "n" && !x.Parts!.Any(p => p.Status == "done");

        var marked = RelationPredicates.Mark(predicate, Navigations);
        var filter = (LogicalFilter)FilterInterpreter.Interpret(marked);

        var negated = (LogicalFilter)filter.Operands[1];
        var exists = (FunctionFilter)negated.Operands[0];
        Assert.That(exists.Function, Is.EqualTo(RelationPredicates.RelatedRowExistsFunction));
        Assert.That(exists.Member, Is.EqualTo("Id"));
        Assert.That(((RelationPredicate)exists.Arguments[0]!).Navigation.Target, Is.EqualTo(typeof(Part)));
    }
}
