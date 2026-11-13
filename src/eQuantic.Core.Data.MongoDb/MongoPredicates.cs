using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using eQuantic.Core.Data.MongoDb.Repository;
using eQuantic.Core.Data.Query;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>
///     Prepares a predicate (or a whole LINQ query) for the driver: an array's <c>Contains</c> rewritten to the shape
///     LINQ providers read, text matched without regard to case rewritten to an escaped case-insensitive regular
///     expression, and each test across a relation resolved into the related keys — the related documents' keys read
///     first (one <c>distinct</c> per test, through the driver, inside the open transaction's session), then an
///     <c>$in</c> over them.
///     <para>
///         A navigation follows the conventions an include follows: a collection of documents of another collection
///         holding a foreign key back to the owner (<c>[ForeignKey("Owner")]</c> or <c>{Owner}Id</c>), or a reference
///         whose foreign key the owner holds (<c>[ForeignKey("Nav")]</c> or <c>{Nav}Id</c>). A class-typed member
///         without one is an embedded document, read as such.
///     </para>
/// </summary>
internal static class MongoPredicates
{
    /// <summary>The navigation a member is, or <c>null</c> (an embedded document or array, a scalar).</summary>
    public static RelationNavigation? Navigation(Type owner, PropertyInfo member)
    {
        if (member.PropertyType == typeof(string) || !owner.IsClass)
        {
            return null;
        }

        if (RelationCountInterpreter.ElementType(member.PropertyType) is { IsClass: true } element && element != typeof(string))
        {
            return MongoInclude.ForeignKey(element, owner.Name) is { } foreignKey && MongoInclude.TryPrimaryKey(owner) is { } ownerKey
                ? new RelationNavigation(owner, member, element, isCollection: true, ownerKey, foreignKey)
                : null;
        }

        if (!member.PropertyType.IsClass || RelationCountInterpreter.ElementType(member.PropertyType) is not null)
        {
            return null;
        }

        return MongoInclude.ForeignKey(owner, member.Name) is { } referenceKey && MongoInclude.TryPrimaryKey(member.PropertyType) is { } targetKey
            ? new RelationNavigation(owner, member, member.PropertyType, isCollection: false, referenceKey, targetKey)
            : null;
    }

    /// <summary>Whether a query crosses a relation (and so reads related keys before it runs).</summary>
    public static bool Crosses(Expression query) => RelationPredicates.Crosses(query, Navigation);

    /// <summary>The query ready for the driver: normalized, case-insensitive text as regular expressions, relations resolved.</summary>
    public static async Task<IQueryable<T>> ReadyAsync<T>(IQueryable<T> query, MongoUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        var expression = Rewrite(query.Expression);
        expression = await RelationPredicates.ResolveQueryAsync(expression, Navigation, Keys(unitOfWork), cancellationToken).ConfigureAwait(false);
        return ReferenceEquals(expression, query.Expression) ? query : query.Provider.CreateQuery<T>(expression);
    }

    /// <summary>
    ///     The query ready for the driver, for a synchronous read: what <see cref="ReadyAsync{T}" /> does, the related keys
    ///     read through the driver's synchronous API.
    /// </summary>
    public static IQueryable<T> Ready<T>(IQueryable<T> query, MongoUnitOfWork unitOfWork)
    {
        var expression = Rewrite(query.Expression);
        var resolution = RelationPredicates.ResolveQueryAsync(expression, Navigation, SynchronousKeys(unitOfWork), CancellationToken.None);
        // Every read above completes synchronously (the driver's own synchronous API), so the task has already finished.
        expression = resolution.GetAwaiter().GetResult();
        return ReferenceEquals(expression, query.Expression) ? query : query.Provider.CreateQuery<T>(expression);
    }

    /// <summary>The filter ready for a set-based write: normalized, case-insensitive text as regular expressions, relations resolved.</summary>
    public static async Task<Expression<Func<T, bool>>> ReadyAsync<T>(Expression<Func<T, bool>> filter, MongoUnitOfWork unitOfWork,
        CancellationToken cancellationToken)
    {
        var rewritten = (Expression<Func<T, bool>>)Rewrite(filter);
        return await RelationPredicates.ResolveAsync(rewritten, Navigation, Keys(unitOfWork), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A predicate the driver renders on its own (a <c>$lookup</c>'s <c>$match</c>): normalized and case-insensitive text rewritten.</summary>
    public static LambdaExpression Rendered(LambdaExpression predicate) => (LambdaExpression)Rewrite(predicate);

    /// <summary>The explanation of what a query's relations read first (no read runs).</summary>
    public static IReadOnlyList<string> Notes(Expression query)
    {
        var notes = new List<string>();
        new QueryLambdas(lambda =>
        {
            foreach (var relation in RelationPredicates.Find(lambda, Navigation))
            {
                notes.Add($"'{relation.Text}' resolves first: the distinct {relation.Navigation.TargetKey.Name} of the " +
                          $"'{MongoModeling.CollectionName(relation.Navigation.Target)}' documents that match is read (one distinct, " +
                          $"inside the transaction's session when one is open), and the read takes {relation.Navigation.OwnerKey.Name} " +
                          $"{(relation.Negated ? "$nin" : "$in")} those keys.");
            }
        }).Visit(query);
        return notes;
    }

    /// <summary>The query with every relation replaced by an empty key list, for a plan that runs nothing.</summary>
    public static Expression Unresolved(Expression query)
    {
        var expression = Rewrite(query);
        return RelationPredicates.ResolveQueryAsync(expression, Navigation,
            (_, _, _, _) => Task.FromResult<IReadOnlyCollection<object?>>([]), CancellationToken.None).GetAwaiter().GetResult();
    }

    private static RelatedKeysReader Keys(MongoUnitOfWork unitOfWork) => async (target, condition, member, cancellationToken) =>
        await ((Task<IReadOnlyCollection<object?>>)typeof(MongoPredicates)
            .GetMethod(nameof(DistinctAsync), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(target, member.PropertyType)
            .Invoke(null, [unitOfWork, condition, member, cancellationToken])!).ConfigureAwait(false);

    private static RelatedKeysReader SynchronousKeys(MongoUnitOfWork unitOfWork) => (target, condition, member, _) =>
        Task.FromResult((IReadOnlyCollection<object?>)typeof(MongoPredicates)
            .GetMethod(nameof(Distinct), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(target, member.PropertyType)
            .Invoke(null, [unitOfWork, condition, member])!);

    /// <summary>The related documents' query: the target's global filter and the relation's condition, in the open session.</summary>
    private static IQueryable<TTarget> Related<TTarget>(MongoUnitOfWork unitOfWork, LambdaExpression? condition) where TTarget : class
    {
        var collection = unitOfWork.GetCollection<TTarget>();
        var session = unitOfWork.Session;
        var query = session is null ? collection.AsQueryable() : collection.AsQueryable(session);
        if (unitOfWork.GlobalFilter<TTarget>() is { } global)
        {
            query = query.Where(global);
        }

        return condition is null ? query : query.Where((Expression<Func<TTarget, bool>>)condition);
    }

    private static Expression<Func<TTarget, TMember>> Selector<TTarget, TMember>(PropertyInfo member)
    {
        var parameter = Expression.Parameter(typeof(TTarget), "related");
        return Expression.Lambda<Func<TTarget, TMember>>(Expression.Property(parameter, member), parameter);
    }

    private static async Task<IReadOnlyCollection<object?>> DistinctAsync<TTarget, TMember>(MongoUnitOfWork unitOfWork,
        LambdaExpression? condition, PropertyInfo member, CancellationToken cancellationToken) where TTarget : class
    {
        var query = await ReadyAsync(Related<TTarget>(unitOfWork, condition), unitOfWork, cancellationToken).ConfigureAwait(false);
        var values = await query.Select(Selector<TTarget, TMember>(member)).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        return values.Cast<object?>().ToList();
    }

    private static IReadOnlyCollection<object?> Distinct<TTarget, TMember>(MongoUnitOfWork unitOfWork, LambdaExpression? condition,
        PropertyInfo member) where TTarget : class
    {
        var query = Ready(Related<TTarget>(unitOfWork, condition), unitOfWork);
        return query.Select(Selector<TTarget, TMember>(member)).Distinct().ToList().Cast<object?>().ToList();
    }

    // ---------------------------------------------------------------- text without regard to case

    /// <summary>Normalizes the expression and rewrites its case-insensitive text tests into regular expressions.</summary>
    private static Expression Rewrite(Expression expression) => new CaseInsensitiveText().Visit(PredicateNormalizer.Normalize(expression));

    /// <summary>
    ///     <c>string.Equals(x.Name, v, OrdinalIgnoreCase)</c>, <c>x.Name.Equals/Contains/StartsWith/EndsWith(v, OrdinalIgnoreCase)</c>
    ///     and the same tests over <c>x.Name.ToLower()</c>/<c>ToUpper()</c> become <c>new Regex(escaped, IgnoreCase).IsMatch(x.Name)</c>,
    ///     which the driver renders as <c>{ Name: /…/i }</c>. A test over a folded text whose value holds a letter of the
    ///     other case matches nothing, as in C#.
    /// </summary>
    private sealed class CaseInsensitiveText : ExpressionVisitor
    {
        private static readonly MethodInfo IsMatch = typeof(Regex).GetMethod(nameof(Regex.IsMatch), [typeof(string)])!;

        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (node.NodeType is ExpressionType.Equal or ExpressionType.NotEqual && node.Left.Type == typeof(string)
                && (Folded(node.Left, node.Right) ?? Folded(node.Right, node.Left)) is { } folded)
            {
                return node.NodeType == ExpressionType.Equal ? folded : Expression.Not(folded);
            }

            return base.VisitBinary(node);
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType != typeof(string))
            {
                return base.VisitMethodCall(node);
            }

            var match = Match(node.Method.Name);
            if (match is null)
            {
                return base.VisitMethodCall(node);
            }

            // string.Equals(a, b, comparison)
            if (node.Object is null && match == TextMatch.Equals && node.Arguments.Count == 3 && IgnoresCase(node.Arguments[2]))
            {
                return Test(node.Arguments[0], node.Arguments[1], TextMatch.Equals, fold: null)
                       ?? Test(node.Arguments[1], node.Arguments[0], TextMatch.Equals, fold: null)
                       ?? base.VisitMethodCall(node);
            }

            if (node.Object is not { } receiver)
            {
                return base.VisitMethodCall(node);
            }

            // x.Name.Op(v, comparison), v.Equals(x.Name, comparison)
            if (node.Arguments.Count == 2 && IgnoresCase(node.Arguments[1]))
            {
                return Test(receiver, node.Arguments[0], match.Value, fold: null)
                       ?? (match == TextMatch.Equals ? Test(node.Arguments[0], receiver, match.Value, fold: null) : null)
                       ?? base.VisitMethodCall(node);
            }

            // x.Name.ToLower().Op(v)
            if (node.Arguments.Count == 1 && Folding(receiver) is { } folding)
            {
                return Test(folding.Member, node.Arguments[0], match.Value, folding.Upper) ?? base.VisitMethodCall(node);
            }

            return base.VisitMethodCall(node);
        }

        private static Expression? Folded(Expression folded, Expression value) =>
            Folding(folded) is { } folding ? Test(folding.Member, value, TextMatch.Equals, folding.Upper) : null;

        /// <summary>The regular expression test of a member against a value, or <c>null</c> when they are not a member and a value.</summary>
        private static Expression? Test(Expression member, Expression value, TextMatch match, bool? fold)
        {
            if (!Reads(member) || !Constant<string>(value, out var text))
            {
                return null;
            }

            if (text is null)
            {
                return match == TextMatch.Equals && fold is null ? Expression.Equal(member, Expression.Constant(null, typeof(string))) : null;
            }

            if (fold is { } upper && text != (upper ? text.ToUpperInvariant() : text.ToLowerInvariant()))
            {
                // A lower-cased text holds no upper-case letter (nor an upper-cased one a lower-case letter).
                return Expression.Constant(false);
            }

            var escaped = Regex.Escape(text);
            var pattern = match switch
            {
                TextMatch.Equals => "^" + escaped + @"\z",
                TextMatch.StartsWith => "^" + escaped,
                TextMatch.EndsWith => escaped + @"\z",
                _ => escaped,
            };
            return Expression.Call(Expression.Constant(new Regex(pattern, RegexOptions.IgnoreCase)), IsMatch, member);
        }

        private static (Expression Member, bool Upper)? Folding(Expression expression) =>
            expression is MethodCallExpression { Object: { } member, Arguments.Count: 0 } call && call.Method.DeclaringType == typeof(string)
                ? call.Method.Name switch
                {
                    nameof(string.ToLower) or nameof(string.ToLowerInvariant) => (member, false),
                    nameof(string.ToUpper) or nameof(string.ToUpperInvariant) => (member, true),
                    _ => null,
                }
                : null;

        private static TextMatch? Match(string method) => method switch
        {
            nameof(string.Equals) => TextMatch.Equals,
            nameof(string.StartsWith) => TextMatch.StartsWith,
            nameof(string.EndsWith) => TextMatch.EndsWith,
            nameof(string.Contains) => TextMatch.Contains,
            _ => null,
        };

        private static bool IgnoresCase(Expression expression) =>
            Constant<object>(expression, out var value) && value is StringComparison.OrdinalIgnoreCase or StringComparison.InvariantCultureIgnoreCase;

        /// <summary>Whether the expression reads a lambda parameter (a member of the document).</summary>
        private static bool Reads(Expression expression)
        {
            var reads = false;
            new ParameterSeeker(() => reads = true).Visit(expression);
            return reads;
        }

        /// <summary>The value of a parameter-free expression.</summary>
        private static bool Constant<T>(Expression expression, out T? value)
        {
            value = default;
            if (Reads(expression))
            {
                return false;
            }

            var evaluated = expression is ConstantExpression constant
                ? constant.Value
                : Expression.Lambda(expression).Compile(preferInterpretation: true).DynamicInvoke();
            if (evaluated is T typed)
            {
                value = typed;
                return true;
            }

            return evaluated is null && default(T) is null;
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
