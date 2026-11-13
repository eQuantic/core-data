using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Core.Data.CosmosDb.Repository;
using eQuantic.Core.Data.Query;
using Microsoft.Azure.Cosmos.Linq;

namespace eQuantic.Core.Data.CosmosDb;

/// <summary>
///     Prepares a predicate (or a whole LINQ query) for the SDK's LINQ translation: an array's <c>Contains</c> rewritten
///     to the shape the SDK reads (C# 14 binds it to <c>MemoryExtensions</c> over a span, which the SDK cannot evaluate),
///     <c>string.Equals(a, b, comparison)</c> rewritten to the instance form the SDK renders as <c>STRINGEQUALS(…, true)</c>,
///     and each test across a relation resolved into the related keys — a relation crosses containers, which Cosmos DB
///     cannot join: the related documents' keys are read first (<c>SELECT DISTINCT VALUE</c>, one query per test), then
///     an <c>IN</c> over them.
///     <para>
///         A navigation follows the conventions an include follows, over entities the model registers: a collection of
///         documents holding a foreign key back to the owner (<c>[ForeignKey("Owner")]</c> or <c>{Owner}Id</c>), or a
///         reference whose foreign key the owner holds (<c>{Nav}Id</c>), naming the related document's id.
///     </para>
/// </summary>
internal static class CosmosPredicates
{
    private const BindingFlags Members = BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance;

    /// <summary>The navigations of the entities a unit of work maps.</summary>
    public static Func<Type, PropertyInfo, RelationNavigation?> Navigations(CosmosUnitOfWork unitOfWork) => (owner, member) =>
    {
        if (member.PropertyType == typeof(string) || !unitOfWork.Maps(owner))
        {
            return null;
        }

        if (RelationCountInterpreter.ElementType(member.PropertyType) is { IsClass: true } element && element != typeof(string))
        {
            return unitOfWork.Maps(element) && CosmosInclude.ForeignKey(element, owner.Name) is { } foreignKey && Key(owner) is { } ownerKey
                ? new RelationNavigation(owner, member, element, isCollection: true, ownerKey, foreignKey)
                : null;
        }

        return unitOfWork.Maps(member.PropertyType) && CosmosInclude.ForeignKey(owner, member.Name) is { } referenceKey
                                                    && Key(member.PropertyType) is { } targetKey
            ? new RelationNavigation(owner, member, member.PropertyType, isCollection: false, referenceKey, targetKey)
            : null;
    };

    private static PropertyInfo? Key(Type type) => type.GetProperty("Id", Members);

    /// <summary>The query ready for the SDK: normalized, and each relation resolved into the related keys (read first).</summary>
    public static async Task<IQueryable<T>> ReadyAsync<T>(IQueryable<T> query, CosmosUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        var expression = Rewrite(query.Expression);
        expression = await RelationPredicates.ResolveQueryAsync(expression, Navigations(unitOfWork), Keys(unitOfWork), cancellationToken)
            .ConfigureAwait(false);
        return ReferenceEquals(expression, query.Expression) ? query : query.Provider.CreateQuery<T>(expression);
    }

    /// <summary>The filter ready for a set-based write: normalized, and each relation resolved into the related keys.</summary>
    public static async Task<Expression<Func<T, bool>>> ReadyAsync<T>(Expression<Func<T, bool>> filter, CosmosUnitOfWork unitOfWork,
        CancellationToken cancellationToken) =>
        await RelationPredicates.ResolveAsync((Expression<Func<T, bool>>)Rewrite(filter), Navigations(unitOfWork), Keys(unitOfWork),
            cancellationToken).ConfigureAwait(false);

    /// <summary>A predicate the SDK translates on its own (an include's filter, a count's): normalized.</summary>
    public static Expression<Func<T, bool>> Rendered<T>(Expression<Func<T, bool>> predicate) => (Expression<Func<T, bool>>)Rewrite(predicate);

    /// <summary>The query with every relation replaced by an empty key list, for a plan that runs nothing.</summary>
    public static IQueryable<T> Unresolved<T>(IQueryable<T> query, CosmosUnitOfWork unitOfWork)
    {
        var expression = RelationPredicates.ResolveQueryAsync(Rewrite(query.Expression), Navigations(unitOfWork),
            (_, _, _, _) => Task.FromResult<IReadOnlyCollection<object?>>([]), CancellationToken.None).GetAwaiter().GetResult();
        return ReferenceEquals(expression, query.Expression) ? query : query.Provider.CreateQuery<T>(expression);
    }

    /// <summary>What a query's relations read first, for a plan (no read runs).</summary>
    public static IReadOnlyList<string> Notes(Expression query, CosmosUnitOfWork unitOfWork)
    {
        var notes = new List<string>();
        var navigations = Navigations(unitOfWork);
        new QueryLambdas(lambda =>
        {
            foreach (var relation in RelationPredicates.Find(lambda, navigations))
            {
                notes.Add($"'{relation.Text}' resolves first: the related container ('{unitOfWork.EntityModel.For(relation.Navigation.Target).ContainerName}') " +
                          $"is read for the distinct {relation.Navigation.TargetKey.Name} of the documents that match (SELECT DISTINCT VALUE, " +
                          $"one query, cross-partition), and this read takes {relation.Navigation.OwnerKey.Name} " +
                          $"{(relation.Negated ? "NOT IN" : "IN")} those keys; Explain runs nothing, so the list shows empty here.");
            }
        }).Visit(query);
        return notes;
    }

    private static RelatedKeysReader Keys(CosmosUnitOfWork unitOfWork) => async (target, condition, member, cancellationToken) =>
        await ((Task<IReadOnlyCollection<object?>>)typeof(CosmosPredicates)
            .GetMethod(nameof(DistinctAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(target, member.PropertyType)
            .Invoke(null, [unitOfWork, condition, member, cancellationToken])!).ConfigureAwait(false);

    private static async Task<IReadOnlyCollection<object?>> DistinctAsync<TTarget, TMember>(CosmosUnitOfWork unitOfWork,
        LambdaExpression? condition, PropertyInfo member, CancellationToken cancellationToken) where TTarget : class
    {
        IQueryable<TTarget> query = unitOfWork.GetContainer<TTarget>().GetItemLinqQueryable<TTarget>();
        if (unitOfWork.GlobalFilter<TTarget>() is { } global)
        {
            query = query.Where(global);
        }

        if (condition is not null)
        {
            query = query.Where((Expression<Func<TTarget, bool>>)condition);
        }

        query = await ReadyAsync(query, unitOfWork, cancellationToken).ConfigureAwait(false);
        var parameter = Expression.Parameter(typeof(TTarget), "related");
        var selector = Expression.Lambda<Func<TTarget, TMember>>(Expression.Property(parameter, member), parameter);

        var values = new List<object?>();
        using var iterator = query.Select(selector).Distinct().ToFeedIterator();
        while (iterator.HasMoreResults)
        {
            foreach (var value in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                values.Add(value);
            }
        }

        return values;
    }

    // ---------------------------------------------------------------- rewriting

    /// <summary>Normalizes the expression for the SDK's translation.</summary>
    private static Expression Rewrite(Expression expression) => new SdkShapes().Visit(PredicateNormalizer.Normalize(expression));

    /// <summary>
    ///     <c>string.Equals(a, b, comparison)</c> → <c>member.Equals(value, comparison)</c> (the SDK reads only the instance
    ///     form), and <c>ToLowerInvariant()</c>/<c>ToUpperInvariant()</c> → <c>ToLower()</c>/<c>ToUpper()</c> (Cosmos DB's
    ///     <c>LOWER</c>/<c>UPPER</c> are culture-invariant).
    /// </summary>
    private sealed class SdkShapes : ExpressionVisitor
    {
        private static readonly MethodInfo InstanceEquals = typeof(string).GetMethod(nameof(string.Equals), [typeof(string), typeof(StringComparison)])!;
        private static readonly MethodInfo ToLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
        private static readonly MethodInfo ToUpper = typeof(string).GetMethod(nameof(string.ToUpper), Type.EmptyTypes)!;

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var visited = (MethodCallExpression)base.VisitMethodCall(node);
            if (visited.Method.DeclaringType != typeof(string))
            {
                return visited;
            }

            if (visited is { Object: null, Method.Name: nameof(string.Equals), Arguments.Count: 3 })
            {
                var (member, value) = Reads(visited.Arguments[0]) ? (visited.Arguments[0], visited.Arguments[1]) : (visited.Arguments[1], visited.Arguments[0]);
                return Expression.Call(member, InstanceEquals, value, visited.Arguments[2]);
            }

            return visited switch
            {
                { Object: { } receiver, Method.Name: nameof(string.ToLowerInvariant), Arguments.Count: 0 } => Expression.Call(receiver, ToLower),
                { Object: { } receiver, Method.Name: nameof(string.ToUpperInvariant), Arguments.Count: 0 } => Expression.Call(receiver, ToUpper),
                _ => visited,
            };
        }

        private static bool Reads(Expression expression)
        {
            var reads = false;
            new ParameterSeeker(() => reads = true).Visit(expression);
            return reads;
        }

        private sealed class ParameterSeeker(Action found) : ExpressionVisitor
        {
            protected override Expression VisitParameter(ParameterExpression node)
            {
                found();
                return node;
            }
        }
    }

    /// <summary>Hands every predicate lambda of a query to a callback.</summary>
    private sealed class QueryLambdas(Action<LambdaExpression> found) : ExpressionVisitor
    {
        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            if (node.ReturnType == typeof(bool) && node.Parameters.Count == 1)
            {
                found(node);
            }

            return base.VisitLambda(node);
        }
    }
}
