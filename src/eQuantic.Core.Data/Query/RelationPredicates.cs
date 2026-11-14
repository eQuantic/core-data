using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Query;

/// <summary>
///     A navigation a filter can cross, as an engine's model declares it: the member, the related entity, and the two
///     keys that relate the rows. A related row is the owner's when <c>row.TargetKey == owner.OwnerKey</c> — for a
///     collection (<c>x.Mentions</c>) the owner's key and the related rows' foreign key back to it, for a reference
///     (<c>x.Repository</c>) the owner's foreign key and the related row's key.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class RelationNavigation
{
    /// <summary>Describes a navigation.</summary>
    /// <param name="owner">The entity declaring the navigation.</param>
    /// <param name="member">The navigation member.</param>
    /// <param name="target">The related entity (a collection's element type).</param>
    /// <param name="isCollection">Whether the navigation is a collection (<c>true</c>) or a reference.</param>
    /// <param name="ownerKey">The member of the owner a related row's <paramref name="targetKey" /> equals.</param>
    /// <param name="targetKey">The member of the related entity that names its owner.</param>
    public RelationNavigation(Type owner, PropertyInfo member, Type target, bool isCollection, PropertyInfo ownerKey, PropertyInfo targetKey)
    {
        Owner = owner;
        Member = member;
        Target = target;
        IsCollection = isCollection;
        OwnerKey = ownerKey;
        TargetKey = targetKey;
    }

    /// <summary>The entity declaring the navigation.</summary>
    public Type Owner { get; }

    /// <summary>The navigation member.</summary>
    public PropertyInfo Member { get; }

    /// <summary>The related entity (a collection's element type).</summary>
    public Type Target { get; }

    /// <summary>Whether the navigation is a collection (<c>true</c>) or a reference.</summary>
    public bool IsCollection { get; }

    /// <summary>The member of the owner a related row's <see cref="TargetKey" /> equals.</summary>
    public PropertyInfo OwnerKey { get; }

    /// <summary>The member of the related entity that names its owner.</summary>
    public PropertyInfo TargetKey { get; }
}

/// <summary>
///     One test a filter makes across a relation, normalized to "a related row exists that meets a condition", possibly
///     negated: <c>x.Items.Any(p)</c> is a row meeting <c>p</c>; <c>x.Items.All(p)</c> is no row meeting <c>!p</c>;
///     <c>x.Repository.WorkspaceId == w</c> is a row meeting <c>r =&gt; r.WorkspaceId == w</c>; <c>x.Container == null</c>
///     is no row at all. The condition may cross relations of the related entity in turn.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class RelationPredicate
{
    internal RelationPredicate(RelationNavigation navigation, LambdaExpression? condition, bool negated, string text)
    {
        Navigation = navigation;
        Condition = condition;
        Negated = negated;
        Text = text;
    }

    /// <summary>The navigation crossed.</summary>
    public RelationNavigation Navigation { get; }

    /// <summary>The condition a related row has to meet (over <see cref="RelationNavigation.Target" />), or <c>null</c> for any row.</summary>
    public LambdaExpression? Condition { get; }

    /// <summary>Whether the test is that no such row exists.</summary>
    public bool Negated { get; }

    /// <summary>The filter's own text for the test (for plans and messages).</summary>
    public string Text { get; }

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>
///     Reads the distinct values of a member of the related entity's rows that meet a condition (all rows when it is
///     <c>null</c>) — an engine's own read, its global filters applied, inside the open transaction where it has one.
/// </summary>
/// <param name="entityType">The related entity.</param>
/// <param name="condition">The condition, over <paramref name="entityType" />, or <c>null</c>.</param>
/// <param name="member">The member whose distinct values are read.</param>
/// <param name="cancellationToken">A cancellation token.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public delegate Task<IReadOnlyCollection<object?>> RelatedKeysReader(Type entityType, LambdaExpression? condition, PropertyInfo member,
    CancellationToken cancellationToken);

/// <summary>
///     Finds the tests a filter makes across the relations a model declares, and replaces each with what an engine runs
///     for it: a correlated <c>EXISTS</c> on the relational engines (<see cref="Mark" />), or the related keys read first
///     and an <c>IN</c> over them on the engines whose relations cross containers or tables (<see cref="ResolveAsync" />).
///     <list type="bullet">
///         <item>a collection: <c>x.Items.Any()</c>, <c>x.Items.Any(i =&gt; …)</c>, <c>x.Items.All(i =&gt; …)</c>, negated or not;</item>
///         <item>a reference's members: <c>x.Repository.WorkspaceId == w</c>, and once more <c>x.Repository.Space.WorkspaceId == w</c>
///         (any test that reads only the related row: a comparison, a string match, a call);</item>
///         <item>a missing reference: <c>x.Container == null</c> (no related row — the foreign key unset, or naming no row),
///         <c>x.Container != null</c>.</item>
///     </list>
///     A test that mixes the related row with the entity's own members, or uses a collection navigation otherwise than
///     through <c>Any</c>/<c>All</c>, is refused with the reason.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class RelationPredicates
{
    /// <summary>
    ///     The marker a marked predicate holds in place of a relation test: "a row related through
    ///     <paramref name="relation" /> exists for the owner whose key is <paramref name="ownerKey" />". A relational engine
    ///     renders it as a correlated <c>EXISTS</c>; it has no client-side meaning, and running it throws.
    /// </summary>
    /// <param name="ownerKey">The owner's key (<see cref="RelationNavigation.OwnerKey" />).</param>
    /// <param name="relation">The relation test.</param>
    public static bool RelatedRowExists(object? ownerKey, RelationPredicate relation) =>
        throw new NotSupportedException(
            $"'{relation.Text}' crosses a relation, which runs on the store; it cannot be evaluated client-side.");

    /// <summary>The name of <see cref="RelatedRowExists" />, as a <see cref="FunctionFilter" /> carries it.</summary>
    public const string RelatedRowExistsFunction = nameof(RelatedRowExists);

    /// <summary>The relation tests of a predicate, in order (empty when it crosses none).</summary>
    /// <param name="predicate">The predicate.</param>
    /// <param name="navigations">The model's navigations: the one a member of a type is, or <c>null</c>.</param>
    public static IReadOnlyList<RelationPredicate> Find(LambdaExpression predicate, Func<Type, PropertyInfo, RelationNavigation?> navigations)
    {
        var found = new List<RelationPredicate>();
        new Rewriter(predicate.Parameters[0], navigations, (relation, _) =>
        {
            found.Add(relation);
            return null;
        }).Visit(predicate.Body);
        return found;
    }

    /// <summary>
    ///     Replaces each relation test with the <see cref="RelatedRowExists" /> marker (negated where the test is), for an
    ///     engine that renders the relation inside its own statement.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="predicate">The predicate.</param>
    /// <param name="navigations">The model's navigations.</param>
    public static Expression<Func<TEntity, bool>> Mark<TEntity>(Expression<Func<TEntity, bool>> predicate,
        Func<Type, PropertyInfo, RelationNavigation?> navigations) =>
        (Expression<Func<TEntity, bool>>)Mark((LambdaExpression)predicate, navigations);

    /// <summary>Replaces each relation test with the <see cref="RelatedRowExists" /> marker (untyped).</summary>
    /// <param name="predicate">The predicate.</param>
    /// <param name="navigations">The model's navigations.</param>
    public static LambdaExpression Mark(LambdaExpression predicate, Func<Type, PropertyInfo, RelationNavigation?> navigations)
    {
        var replaced = false;
        var body = new Rewriter(predicate.Parameters[0], navigations, (relation, ownerKey) =>
        {
            replaced = true;
            Expression exists = Expression.Call(typeof(RelationPredicates).GetMethod(nameof(RelatedRowExists))!,
                Expression.Convert(ownerKey, typeof(object)), Expression.Constant(relation));
            return relation.Negated ? Expression.Not(exists) : exists;
        }).Visit(predicate.Body);
        return replaced ? Expression.Lambda(predicate.Type, body, predicate.Parameters) : predicate;
    }

    /// <summary>
    ///     Resolves each relation test into the keys it stands for: the distinct <see cref="RelationNavigation.TargetKey" />
    ///     values of the related rows meeting its condition are read first (<paramref name="keys" />, one read per test, in
    ///     order), and the test becomes <c>keys.Contains(x.OwnerKey)</c> — negated where the test is, and then also true
    ///     for an owner whose key is unset. A predicate that crosses no relation is returned as it is, and nothing is read.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="predicate">The predicate.</param>
    /// <param name="navigations">The model's navigations.</param>
    /// <param name="keys">The engine's read of related keys.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static async Task<Expression<Func<TEntity, bool>>> ResolveAsync<TEntity>(Expression<Func<TEntity, bool>> predicate,
        Func<Type, PropertyInfo, RelationNavigation?> navigations, RelatedKeysReader keys, CancellationToken cancellationToken) =>
        (Expression<Func<TEntity, bool>>)await ResolveAsync((LambdaExpression)predicate, navigations, keys, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Resolves each relation test into the keys it stands for (untyped).</summary>
    /// <param name="predicate">The predicate.</param>
    /// <param name="navigations">The model's navigations.</param>
    /// <param name="keys">The engine's read of related keys.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static async Task<LambdaExpression> ResolveAsync(LambdaExpression predicate, Func<Type, PropertyInfo, RelationNavigation?> navigations,
        RelatedKeysReader keys, CancellationToken cancellationToken)
    {
        var relations = Find(predicate, navigations);
        if (relations.Count == 0)
        {
            return predicate;
        }

        // One read per test, in order: an engine's session (MongoDB's) runs one operation at a time.
        var read = new List<IReadOnlyCollection<object?>>(relations.Count);
        foreach (var relation in relations)
        {
            read.Add(await keys(relation.Navigation.Target, relation.Condition, relation.Navigation.TargetKey, cancellationToken)
                .ConfigureAwait(false));
        }

        var next = 0;
        var body = new Rewriter(predicate.Parameters[0], navigations, (relation, ownerKey) => In(relation, ownerKey, read[next++]))
            .Visit(predicate.Body);
        return Expression.Lambda(predicate.Type, body, predicate.Parameters);
    }

    /// <summary>
    ///     Resolves the relation tests of every predicate a query expression holds (<c>Where</c>, and the predicate of
    ///     <c>Any</c>, <c>All</c>, <c>Count</c>, <c>First</c> and the like) — for an engine that hands a whole LINQ query to
    ///     its driver. A query that crosses no relation is returned as it is.
    /// </summary>
    /// <param name="query">The query expression.</param>
    /// <param name="navigations">The model's navigations.</param>
    /// <param name="keys">The engine's read of related keys.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static async Task<Expression> ResolveQueryAsync(Expression query, Func<Type, PropertyInfo, RelationNavigation?> navigations,
        RelatedKeysReader keys, CancellationToken cancellationToken)
    {
        var predicates = new List<LambdaExpression>();
        new QueryPredicates(lambda =>
        {
            if (Find(lambda, navigations).Count > 0)
            {
                predicates.Add(lambda);
            }

            return lambda;
        }).Visit(query);
        if (predicates.Count == 0)
        {
            return query;
        }

        var resolved = new Dictionary<LambdaExpression, LambdaExpression>(ReferenceEqualityComparer.Instance);
        foreach (var predicate in predicates)
        {
            resolved[predicate] = await ResolveAsync(predicate, navigations, keys, cancellationToken).ConfigureAwait(false);
        }

        return new QueryPredicates(lambda => resolved.TryGetValue(lambda, out var replacement) ? replacement : lambda).Visit(query);
    }

    /// <summary>Whether a query expression holds a predicate that crosses a relation.</summary>
    /// <param name="query">The query expression.</param>
    /// <param name="navigations">The model's navigations.</param>
    public static bool Crosses(Expression query, Func<Type, PropertyInfo, RelationNavigation?> navigations)
    {
        var crosses = false;
        new QueryPredicates(lambda =>
        {
            crosses |= Find(lambda, navigations).Count > 0;
            return lambda;
        }).Visit(query);
        return crosses;
    }

    /// <summary><c>keys.Contains(owner.Key)</c>, negated (and then true for an unset key) where the test is.</summary>
    private static Expression In(RelationPredicate relation, Expression ownerKey, IReadOnlyCollection<object?> keys)
    {
        var type = ownerKey.Type;
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
        foreach (var key in keys)
        {
            if (key is null)
            {
                continue;
            }

            list.Add(underlying.IsInstanceOfType(key) ? key : Convert.ChangeType(key, underlying, System.Globalization.CultureInfo.InvariantCulture));
        }

        Expression contains = Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [type], Expression.Constant(list), ownerKey);
        if (!relation.Negated)
        {
            return contains;
        }

        Expression none = Expression.Not(contains);
        return type.IsValueType && Nullable.GetUnderlyingType(type) is null
            ? none
            : Expression.OrElse(Expression.Equal(ownerKey, Expression.Constant(null, type)), none);
    }

    // ---------------------------------------------------------------- the walk

    /// <summary>
    ///     Walks a predicate's boolean structure and hands each relation test it meets to <c>replace</c> with the
    ///     expression of the owner's key; what <c>replace</c> answers takes the test's place (<c>null</c> leaves it).
    /// </summary>
    private sealed class Rewriter(ParameterExpression root, Func<Type, PropertyInfo, RelationNavigation?> navigations,
        Func<RelationPredicate, Expression, Expression?> replace)
    {
        public Expression Visit(Expression node)
        {
            switch (node)
            {
                case BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.OrElse or ExpressionType.And or ExpressionType.Or } logical
                    when logical.Type == typeof(bool):
                    return logical.Update(Visit(logical.Left), logical.Conversion, Visit(logical.Right));
                case UnaryExpression { NodeType: ExpressionType.Not } not when not.Type == typeof(bool):
                    return not.Update(Visit(not.Operand));
                default:
                    return Atom(node);
            }
        }

        private Expression Atom(Expression atom)
        {
            if (Quantifier(atom) is { } quantified)
            {
                return replace(quantified.Relation, quantified.OwnerKey) ?? atom;
            }

            if (MissingReference(atom) is { } missing)
            {
                return replace(missing.Relation, missing.OwnerKey) ?? atom;
            }

            if (ReferenceTest(atom) is { } reference)
            {
                return replace(reference.Relation, reference.OwnerKey) ?? atom;
            }

            return atom;
        }

        /// <summary><c>x.Items.Any()</c>, <c>x.Items.Any(p)</c>, <c>x.Items.All(p)</c> over a collection navigation of the entity.</summary>
        private (RelationPredicate Relation, Expression OwnerKey)? Quantifier(Expression atom)
        {
            if (Strip(atom) is not MethodCallExpression { Method.DeclaringType: { } declaring } call
                || (declaring != typeof(Enumerable) && declaring != typeof(Queryable))
                || call.Method.Name is not (nameof(Enumerable.Any) or nameof(Enumerable.All))
                || Chain(call.Arguments[0]) is not { } chain
                || FirstNavigation(chain) is not { } first
                || first.Index != chain.Count - 1
                || !first.Navigation.IsCollection)
            {
                return null;
            }

            var all = call.Method.Name == nameof(Enumerable.All);
            var condition = call.Arguments.Count > 1
                ? Condition(call.Arguments[1], $"{first.Navigation.Member.Name}.{call.Method.Name}")
                : null;
            if (condition is not null && References(condition.Body, root))
            {
                throw new NotSupportedException(
                    $"'{atom}' compares the related rows with the entity's own members; a relation predicate tests the related rows " +
                    "against values only.");
            }

            if (all)
            {
                condition = condition is null
                    ? throw new NotSupportedException($"'{atom}': All needs a condition.")
                    : Expression.Lambda(Expression.Not(condition.Body), condition.Parameters);
            }

            var ownerKey = OwnerKey(chain, first);
            return (new RelationPredicate(first.Navigation, condition, all, atom.ToString()), ownerKey);
        }

        /// <summary><c>x.Container == null</c> / <c>!= null</c> over a reference navigation of the entity.</summary>
        private (RelationPredicate Relation, Expression OwnerKey)? MissingReference(Expression atom)
        {
            if (Strip(atom) is not BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } comparison)
            {
                return null;
            }

            var side = IsNull(comparison.Right) ? comparison.Left : IsNull(comparison.Left) ? comparison.Right : null;
            if (side is null || Chain(side) is not { } chain || FirstNavigation(chain) is not { } first || first.Index != chain.Count - 1)
            {
                return null;
            }

            if (first.Navigation.IsCollection)
            {
                throw new NotSupportedException(
                    $"'{atom}' compares a collection navigation to null; test its rows with Any() or !Any().");
            }

            var missing = comparison.NodeType == ExpressionType.Equal;
            return (new RelationPredicate(first.Navigation, null, missing, atom.ToString()), OwnerKey(chain, first));
        }

        /// <summary>Any other test that reads a reference navigation's row: the test, over the related row.</summary>
        private (RelationPredicate Relation, Expression OwnerKey)? ReferenceTest(Expression atom)
        {
            var chains = new List<List<MemberExpression>>();
            var own = false;
            new ChainCollector(root, chains, () => own = true).Visit(atom);

            (List<MemberExpression> Chain, (int Index, RelationNavigation Navigation) First)? crossing = null;
            foreach (var chain in chains)
            {
                if (FirstNavigation(chain) is not { } first)
                {
                    own = true;
                    continue;
                }

                if (first.Navigation.IsCollection)
                {
                    throw new NotSupportedException(
                        $"'{atom}' uses the collection navigation '{first.Navigation.Member.Name}' otherwise than through Any() or All(), " +
                        "which is how a filter tests a collection's rows.");
                }

                if (first.Index == chain.Count - 1)
                {
                    throw new NotSupportedException(
                        $"'{atom}' uses the reference navigation '{first.Navigation.Member.Name}' as a value; compare its members, or it to null.");
                }

                if (crossing is { } other && !SamePrefix(other.Chain, chain, first.Index))
                {
                    throw new NotSupportedException($"'{atom}' reads two different relations in one test; test each on its own.");
                }

                crossing ??= (chain, first);
            }

            if (crossing is not { } relation)
            {
                return null;
            }

            if (own)
            {
                throw new NotSupportedException(
                    $"'{atom}' compares a related row with the entity's own members; a relation predicate tests the related row against values only.");
            }

            var navigation = relation.First.Navigation;
            var target = Expression.Parameter(navigation.Target, "related");
            var prefix = relation.Chain[relation.First.Index];
            var body = new PrefixReplacer(prefix, target).Visit(atom);
            return (new RelationPredicate(navigation, Expression.Lambda(body, target), false, atom.ToString()),
                OwnerKey(relation.Chain, relation.First));
        }

        /// <summary>The owner's key: the owner of the navigation (the root, or what the chain reached before it), then its key.</summary>
        private Expression OwnerKey(List<MemberExpression> chain, (int Index, RelationNavigation Navigation) first)
        {
            var owner = first.Index == 0 ? (Expression)root : chain[first.Index - 1];
            return Expression.Property(owner, first.Navigation.OwnerKey);
        }

        /// <summary>The member chain an expression is, rooted at the predicate's parameter (outermost first), or <c>null</c>.</summary>
        private List<MemberExpression>? Chain(Expression expression)
        {
            var chain = new List<MemberExpression>();
            var current = Strip(expression);
            while (current is MemberExpression member)
            {
                chain.Insert(0, member);
                current = member.Expression is null ? null : Strip(member.Expression);
            }

            return current == root && chain.Count > 0 ? chain : null;
        }

        private (int Index, RelationNavigation Navigation)? FirstNavigation(List<MemberExpression> chain)
        {
            for (var index = 0; index < chain.Count; index++)
            {
                if (chain[index].Member is PropertyInfo property
                    && navigations(chain[index].Expression!.Type, property) is { } navigation)
                {
                    return (index, navigation);
                }
            }

            return null;
        }

        private static bool SamePrefix(List<MemberExpression> left, List<MemberExpression> right, int through) =>
            left.Count > through && right.Count > through
                                 && Enumerable.Range(0, through + 1).All(index => left[index].Member == right[index].Member);
    }

    /// <summary>Collects the member chains rooted at the parameter, and notes a bare use of it.</summary>
    private sealed class ChainCollector(ParameterExpression root, List<List<MemberExpression>> chains, Action bare) : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            var chain = new List<MemberExpression>();
            Expression? current = node;
            while (current is MemberExpression member)
            {
                chain.Insert(0, member);
                current = member.Expression is null ? null : Strip(member.Expression);
            }

            if (current == root)
            {
                chains.Add(chain);
                return node;
            }

            return base.VisitMember(node);
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == root)
            {
                bare();
            }

            return node;
        }
    }

    /// <summary>Puts the related entity's parameter in place of the chain reaching it (<c>x.Repository</c> → <c>related</c>).</summary>
    private sealed class PrefixReplacer(MemberExpression prefix, ParameterExpression target) : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node) =>
            Same(node, prefix) ? (target.Type == node.Type ? target : Expression.Convert(target, node.Type)) : base.VisitMember(node);

        private static bool Same(Expression? left, Expression? right)
        {
            left = left is null ? null : Strip(left);
            right = right is null ? null : Strip(right);
            return (left, right) switch
            {
                (MemberExpression a, MemberExpression b) => a.Member == b.Member && Same(a.Expression, b.Expression),
                (ParameterExpression a, ParameterExpression b) => a == b,
                _ => false,
            };
        }
    }

    /// <summary>Rewrites the predicates a query expression hands its query operators.</summary>
    private sealed class QueryPredicates(Func<LambdaExpression, LambdaExpression> rewrite) : ExpressionVisitor
    {
        private static readonly HashSet<string> PredicateOperators = new(StringComparer.Ordinal)
        {
            nameof(Queryable.Where), nameof(Queryable.Any), nameof(Queryable.All), nameof(Queryable.Count), nameof(Queryable.LongCount),
            nameof(Queryable.First), nameof(Queryable.FirstOrDefault), nameof(Queryable.Single), nameof(Queryable.SingleOrDefault),
            nameof(Queryable.Last), nameof(Queryable.LastOrDefault),
        };

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var visited = (MethodCallExpression)base.VisitMethodCall(node);
            if (visited.Method.DeclaringType?.Name is not ("Queryable" or "MongoQueryable" or "CosmosLinqExtensions")
                || !PredicateOperators.Contains(visited.Method.Name.Replace("Async", string.Empty, StringComparison.Ordinal)))
            {
                return visited;
            }

            var arguments = visited.Arguments.ToArray();
            var changed = false;
            for (var index = 1; index < arguments.Length; index++)
            {
                if (Strip(arguments[index]) is LambdaExpression { ReturnType: var returns } lambda && returns == typeof(bool)
                    && lambda.Parameters.Count == 1)
                {
                    var rewritten = rewrite(lambda);
                    if (!ReferenceEquals(rewritten, lambda))
                    {
                        arguments[index] = arguments[index] is UnaryExpression { NodeType: ExpressionType.Quote }
                            ? Expression.Quote(rewritten)
                            : rewritten;
                        changed = true;
                    }
                }
            }

            return changed ? visited.Update(visited.Object, arguments) : visited;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static LambdaExpression? Lambda(Expression expression) => Strip(expression) as LambdaExpression;

    /// <summary>
    ///     The condition a test of a navigation's rows passes, as an expression the store can run: a lambda written inline,
    ///     or an <c>Expression&lt;Func&lt;…&gt;&gt;</c> held in a variable, which is read. A delegate (a <c>Func</c> variable, a
    ///     compiled expression, a method group) has no body to read, and dropping it would answer another question (whether
    ///     any row is related at all): it is refused, naming the navigation.
    /// </summary>
    /// <param name="argument">The condition argument.</param>
    /// <param name="test">The test, as the message names it (<c>Items.Any</c>).</param>
    internal static LambdaExpression Condition(Expression argument, string test)
    {
        var stripped = Strip(argument);
        if (stripped is LambdaExpression lambda)
        {
            return lambda;
        }

        if (typeof(LambdaExpression).IsAssignableFrom(stripped.Type) && !ReadsParameter(stripped)
                                                                     && Evaluate(stripped) is LambdaExpression held)
        {
            return held;
        }

        throw new NotSupportedException(
            $"'{test}' takes its condition as a delegate ('{argument}': a Func variable, a compiled expression or a method group), " +
            $"whose body cannot be read, so it cannot run in the store and dropping it would test whether any row is related at all. " +
            $"Write the lambda inline: x.{test}(item => …).");
    }

    private static object? Evaluate(Expression expression) =>
        expression is ConstantExpression constant
            ? constant.Value
            : Expression.Lambda<Func<object?>>(Expression.Convert(expression, typeof(object))).Compile(preferInterpretation: true)();

    private static bool ReadsParameter(Expression expression)
    {
        var finder = new AnyParameter();
        finder.Visit(expression);
        return finder.Found;
    }

    private sealed class AnyParameter : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Found = true;
            return node;
        }
    }

    private static bool IsNull(Expression expression) => Strip(expression) is ConstantExpression { Value: null };

    private static bool References(Expression expression, ParameterExpression parameter)
    {
        var found = false;
        new ParameterFinder(parameter, () => found = true).Visit(expression);
        return found;
    }

    private static Expression Strip(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.Quote } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }

    private sealed class ParameterFinder(ParameterExpression parameter, Action found) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == parameter)
            {
                found();
            }

            return node;
        }
    }
}
