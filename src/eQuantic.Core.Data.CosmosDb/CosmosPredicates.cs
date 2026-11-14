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
        var expression = Rewrite(query.Expression, unitOfWork);
        expression = await RelationPredicates.ResolveQueryAsync(expression, Navigations(unitOfWork), Keys(unitOfWork), cancellationToken)
            .ConfigureAwait(false);
        return ReferenceEquals(expression, query.Expression) ? query : query.Provider.CreateQuery<T>(expression);
    }

    /// <summary>The filter ready for a set-based write: normalized, and each relation resolved into the related keys.</summary>
    public static async Task<Expression<Func<T, bool>>> ReadyAsync<T>(Expression<Func<T, bool>> filter, CosmosUnitOfWork unitOfWork,
        CancellationToken cancellationToken) =>
        await RelationPredicates.ResolveAsync((Expression<Func<T, bool>>)Rewrite(filter, unitOfWork), Navigations(unitOfWork), Keys(unitOfWork),
            cancellationToken).ConfigureAwait(false);

    /// <summary>A predicate the SDK translates on its own (an include's filter, a count's): normalized.</summary>
    public static Expression<Func<T, bool>> Rendered<T>(Expression<Func<T, bool>> predicate, CosmosUnitOfWork unitOfWork) =>
        (Expression<Func<T, bool>>)Rewrite(predicate, unitOfWork);

    /// <summary>The query with every relation replaced by an empty key list, for a plan that runs nothing.</summary>
    public static IQueryable<T> Unresolved<T>(IQueryable<T> query, CosmosUnitOfWork unitOfWork)
    {
        var expression = RelationPredicates.ResolveQueryAsync(Rewrite(query.Expression, unitOfWork), Navigations(unitOfWork),
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

    /// <summary>
    ///     Whether a filter tests text without regard to case (<c>Equals</c>, <c>Contains</c>, <c>StartsWith</c>, <c>EndsWith</c>
    ///     with a comparison that ignores case, or over <c>ToLower()</c>/<c>ToUpper()</c>): what a patch's condition must
    ///     not carry, since the condition evaluator answers it with a refusal that reads as "no longer matches".
    /// </summary>
    /// <param name="filter">The filter, as the SDK translates it.</param>
    public static bool TestsTextIgnoringCase(Expression filter)
    {
        var finder = new TextIgnoringCase();
        finder.Visit(filter);
        return finder.Found;
    }

    private sealed class TextIgnoringCase : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(string)
                && (node.Method.Name is nameof(string.ToLower) or nameof(string.ToUpper) or nameof(string.ToLowerInvariant) or nameof(string.ToUpperInvariant)
                    || (node.Method.Name is nameof(string.Equals) or nameof(string.Contains) or nameof(string.StartsWith) or nameof(string.EndsWith)
                        && node.Arguments.Any(argument => argument.Type == typeof(StringComparison)))))
            {
                Found = true;
            }

            return base.VisitMethodCall(node);
        }
    }

    // ---------------------------------------------------------------- rewriting

    /// <summary>
    ///     Normalizes the expression for the SDK's translation, and rewrites each ordered comparison of an enum stored as its
    ///     labels into membership in the labels whose member satisfies it, since Cosmos DB would compare the labels' text
    ///     where C# compares the members' values.
    /// </summary>
    private static Expression Rewrite(Expression expression, CosmosUnitOfWork unitOfWork) =>
        EnumOrder.RewriteComparisons(new SdkShapes().Visit(PredicateNormalizer.Normalize(expression)), unitOfWork.EntityModel.IsLabelEnum);

    /// <summary>
    ///     <c>string.Equals(a, b, comparison)</c> → <c>member.Equals(value, comparison)</c> (the SDK reads only the instance
    ///     form), and <c>ToLowerInvariant()</c>/<c>ToUpperInvariant()</c> → <c>ToLower()</c>/<c>ToUpper()</c> (Cosmos DB's
    ///     <c>LOWER</c>/<c>UPPER</c> are culture-invariant). Where a null meets a text test, the test is written to answer
    ///     what C# answers, since Cosmos DB's <c>STRINGEQUALS</c>, <c>STARTSWITH</c> and <c>LOWER</c> answer undefined for
    ///     null (and a row whose test is undefined is never returned, negated or not), reading it as the engines read a
    ///     test in memory (<c>PredicateNormalizer.CompileResidual</c>): a member equal to a null value is the member's null
    ///     test, and so is its folded text compared with <c>==</c>; a folded text's <c>Equals(null)</c> is false; two
    ///     members are equal when both are null; a test without a member is left for the SDK to evaluate, as C# does; and
    ///     a match against null is refused, as C# refuses it.
    /// </summary>
    private sealed class SdkShapes : ExpressionVisitor
    {
        private static readonly MethodInfo InstanceEquals = typeof(string).GetMethod(nameof(string.Equals), [typeof(string), typeof(StringComparison)])!;
        private static readonly MethodInfo ToLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
        private static readonly MethodInfo ToUpper = typeof(string).GetMethod(nameof(string.ToUpper), Type.EmptyTypes)!;

        /// <summary>
        ///     The negation of a text equality (<c>!string.Equals(x.Name, v, comparison)</c>, its instance forms,
        ///     <c>!(x.Name.ToLower() == v)</c>) also holds where a member it reads is null or absent: C# answers false to the
        ///     equality there, as Cosmos DB's <c>STRINGEQUALS</c> and <c>LOWER</c> do not (they answer undefined, and so does
        ///     its negation).
        /// </summary>
        protected override Expression VisitUnary(UnaryExpression node)
        {
            var visited = base.VisitUnary(node);
            return visited is UnaryExpression { NodeType: ExpressionType.Not, Operand: var operand } negation && NullGuard(operand) is { } guard
                ? Expression.OrElse(negation, guard)
                : visited;
        }

        /// <summary><c>x.Name.ToLower() != v</c> holds where the member is null or absent, as <c>x.Name != v</c> does.</summary>
        protected override Expression VisitBinary(BinaryExpression node)
        {
            var visited = base.VisitBinary(node);
            if (visited is BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } comparison
                && (Folded(comparison.Left) is { } left && IsNull(comparison.Right) ? left
                    : Folded(comparison.Right) is { } right && IsNull(comparison.Left) ? right : null) is { } folded)
            {
                // null folds to null, and a folded text is never null otherwise: the member's null test (LOWER answers undefined).
                var isNull = Expression.Equal(folded, Expression.Constant(null, typeof(string)));
                return comparison.NodeType == ExpressionType.Equal ? isNull : Expression.Not(isNull);
            }

            return visited is BinaryExpression { NodeType: ExpressionType.NotEqual } inequality
                   && (Folded(inequality.Left) ?? Folded(inequality.Right)) is { } member
                   && (!Reads(inequality.Left) || !Reads(inequality.Right))
                ? Expression.OrElse(inequality, NullOrAbsent(member))
                : visited;
        }

        /// <summary>
        ///     Where the negation of a text equality holds although Cosmos DB answers undefined: a member it reads is null or
        ///     absent (a null receiver fails the test, as the engines read <c>x.Name.Equals(v, comparison)</c>); <c>null</c>
        ///     when it is not a text equality.
        /// </summary>
        private static Expression? NullGuard(Expression expression) => expression switch
        {
            MethodCallExpression { Object: { } receiver, Arguments: [var argument, _] } call when TextEquals(call) is not null =>
                (Reads(receiver), Reads(argument)) switch
                {
                    (true, true) => Expression.OrElse(NullOrAbsent(receiver), NullOrAbsent(argument)),
                    (true, false) => NullOrAbsent(receiver),
                    (false, true) => NullOrAbsent(argument),
                    _ => null,
                },
            BinaryExpression { NodeType: ExpressionType.Equal } equality when !Reads(equality.Left) || !Reads(equality.Right) =>
                (Folded(equality.Left) ?? Folded(equality.Right)) is { } member ? NullOrAbsent(member) : null,
            _ => null,
        };

        /// <summary><c>string.Equals(a, b, comparison)</c> or <c>a.Equals(b, comparison)</c>, or <c>null</c>.</summary>
        private static MethodCallExpression? TextEquals(Expression expression) =>
            expression is MethodCallExpression { Method.Name: nameof(string.Equals) } call && call.Method.DeclaringType == typeof(string)
                                                                                         && call.Arguments.Count == (call.Object is null ? 3 : 2)
                                                                                         && call.Arguments[^1].Type == typeof(StringComparison)
                ? call
                : null;

        /// <summary>The member of <c>x.Name.ToLower()</c> / <c>ToUpper()</c>, or <c>null</c>.</summary>
        private static Expression? Folded(Expression expression) =>
            expression is MethodCallExpression { Object: { } member, Arguments.Count: 0, Method.Name: nameof(string.ToLower) or nameof(string.ToUpper) } call
            && call.Method.DeclaringType == typeof(string) && Reads(member)
                ? member
                : null;

        /// <summary>Whether a parameter-free expression evaluates to null.</summary>
        private static bool IsNull(Expression expression) =>
            !Reads(expression)
            && (expression is ConstantExpression constant
                ? constant.Value
                : Expression.Lambda(Expression.Convert(expression, typeof(object))).Compile(preferInterpretation: true).DynamicInvoke()) is null;

        /// <summary><c>!IS_DEFINED(member) || IS_NULL(member)</c>.</summary>
        private static Expression NullOrAbsent(Expression member)
        {
            var boxed = Expression.Convert(member, typeof(object));
            return Expression.OrElse(
                Expression.Not(Expression.Call(typeof(CosmosLinqExtensions), nameof(CosmosLinqExtensions.IsDefined), null, boxed)),
                Expression.Call(typeof(CosmosLinqExtensions), nameof(CosmosLinqExtensions.IsNull), null, boxed));
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var visited = (MethodCallExpression)base.VisitMethodCall(node);
            if (visited.Method.DeclaringType != typeof(string))
            {
                return visited;
            }

            if (TextEquals(visited) is { } equals)
            {
                var (left, right) = equals.Object is null ? (equals.Arguments[0], equals.Arguments[1]) : (equals.Object, equals.Arguments[0]);
                var comparison = equals.Arguments[^1];
                switch (Reads(left), Reads(right))
                {
                    case (false, false):
                        // No member: the SDK evaluates it, as C# does (a null among the values included).
                        return visited;
                    case (true, true):
                        // Two members: C# holds two nulls equal and a null unequal to any text, where STRINGEQUALS answers
                        // undefined. Written so that it is never undefined, it reads the same under any negation.
                        if (equals.Object is not null)
                        {
                            return visited;
                        }

                        var (leftMissing, rightMissing) = (NullOrAbsent(left), NullOrAbsent(right));
                        return Expression.OrElse(Expression.AndAlso(leftMissing, rightMissing),
                            Expression.AndAlso(Expression.AndAlso(Expression.Not(leftMissing), Expression.Not(rightMissing)),
                                Expression.Call(left, InstanceEquals, right, comparison)));
                    default:
                        var (member, value) = Reads(left) ? (left, right) : (right, left);
                        if (IsNull(value))
                        {
                            // A member equal to null is its null test, on every engine; STRINGEQUALS would answer undefined.
                            return Expression.Equal(member, Expression.Constant(null, typeof(string)));
                        }

                        return equals.Object is null ? Expression.Call(member, InstanceEquals, value, comparison) : visited;
                }
            }

            if (visited is { Method.Name: nameof(string.StartsWith) or nameof(string.EndsWith) or nameof(string.Contains), Object: { } matched }
                && visited.Arguments.Count > 0 && visited.Arguments[0].Type == typeof(string) && Reads(matched) && IsNull(visited.Arguments[0]))
            {
                // C# refuses a match against null; Cosmos DB would answer undefined, which reads as "no row".
                throw new NotSupportedException($"A text match of '{matched}' needs a string value; '{visited.Method.Name}' was given null.");
            }

            if (visited is { Method.Name: nameof(string.Equals), Object: { } folded, Arguments: [var compared] } && Folded(folded) is not null
                && compared.Type == typeof(string) && IsNull(compared))
            {
                // A folded text's Equals(null) is false: a null receiver fails a text test, and a text is never null.
                return Expression.Constant(false);
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
