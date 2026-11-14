using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using eQuantic.Core.Data.MongoDb.Repository;
using eQuantic.Core.Data.Query;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>
///     Prepares a predicate (or a whole LINQ query) for the driver: an array's <c>Contains</c> rewritten to the shape
///     LINQ providers read, text matched without regard to case rewritten to an escaped case-insensitive regular
///     expression, and each test across a relation resolved (<see cref="MongoRelations" />): the related documents' keys
///     read first (one aggregation per test — <c>$match</c>, then <c>$group</c> on the key — through the driver, inside the
///     open transaction's session), then an <c>$in</c> over them; or, when they are more than an <c>$in</c> should carry,
///     a <c>$lookup</c> of the related documents in the read's own aggregation.
///     <para>
///         A navigation follows the conventions an include follows: a collection of documents of another collection
///         holding a foreign key back to the owner (<c>[ForeignKey("Owner")]</c> or <c>{Owner}Id</c>), or a reference
///         whose foreign key the owner holds (<c>[ForeignKey("Nav")]</c> or <c>{Nav}Id</c>). A class-typed member
///         without one is an embedded document, read as such.
///     </para>
/// </summary>
internal static class MongoPredicates
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, PropertyInfo), RelationNavigation?> Navigations = new();

    /// <summary>The navigation a member is, or <c>null</c> (an embedded document or array, a scalar); the conventions are static, so it is cached.</summary>
    public static RelationNavigation? Navigation(Type owner, PropertyInfo member) =>
        Navigations.GetOrAdd((owner, member), static key => Resolve(key.Item1, key.Item2));

    private static RelationNavigation? Resolve(Type owner, PropertyInfo member)
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

    /// <summary>
    ///     The query ready for the driver: normalized, case-insensitive text as regular expressions, relations resolved —
    ///     the related keys read asynchronously, cancelled by <paramref name="cancellationToken" />.
    /// </summary>
    public static async Task<IQueryable<T>> ReadyAsync<T>(IQueryable<T> query, MongoUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        var expression = Rewrite(query.Expression);
        expression = await new MongoRelations(unitOfWork, synchronous: false, cancellationToken)
            .QueryAsync(expression, query.Provider).ConfigureAwait(false);
        return ReferenceEquals(expression, query.Expression) ? query : query.Provider.CreateQuery<T>(expression);
    }

    /// <summary>
    ///     The query ready for the driver, for a synchronous read: what <see cref="ReadyAsync{T}(IQueryable{T}, MongoUnitOfWork, CancellationToken)" /> does, the related keys
    ///     read through the driver's synchronous API.
    /// </summary>
    public static IQueryable<T> Ready<T>(IQueryable<T> query, MongoUnitOfWork unitOfWork)
    {
        var expression = Rewrite(query.Expression);
        var resolution = new MongoRelations(unitOfWork, synchronous: true, CancellationToken.None).QueryAsync(expression, query.Provider);
        // Every read above completes synchronously (the driver's own synchronous API), so the task has already finished.
        expression = resolution.GetAwaiter().GetResult();
        return ReferenceEquals(expression, query.Expression) ? query : query.Provider.CreateQuery<T>(expression);
    }

    /// <summary>
    ///     The filters a set-based write runs, in turn: normalized, case-insensitive text as regular expressions, and its
    ///     relations resolved. One filter, with the related keys as <c>$in</c>, when every relation's keys fit in one; when
    ///     one does not (a write's filter cannot hold a <c>$lookup</c>), the ids of the documents the filter matches are
    ///     read first by one aggregation with the <c>$lookup</c>, and each filter is a batch of them and the filter again,
    ///     each relation the <c>$lookup</c> resolved taken as it held for those documents.
    /// </summary>
    public static Task<IReadOnlyList<FilterDefinition<T>>> WriteFiltersAsync<T>(Expression<Func<T, bool>> filter,
        MongoUnitOfWork unitOfWork, IMongoCollection<T> collection, CancellationToken cancellationToken) =>
        new MongoRelations(unitOfWork, synchronous: false, cancellationToken)
            .WriteFiltersAsync((Expression<Func<T, bool>>)Rewrite(filter), collection);

    /// <summary>A predicate the driver renders on its own (a <c>$lookup</c>'s <c>$match</c>): normalized and case-insensitive text rewritten.</summary>
    public static LambdaExpression Rendered(LambdaExpression predicate) => (LambdaExpression)Rewrite(predicate);

    /// <summary>The explanation of what a query's relations read first (no read runs), naming the collections the unit of work reads.</summary>
    public static IReadOnlyList<string> Notes(Expression query, MongoUnitOfWork unitOfWork)
    {
        var notes = new List<string>();
        new QueryLambdas(lambda =>
        {
            foreach (var relation in RelationPredicates.Find(lambda, Navigation))
            {
                notes.Add($"'{relation.Text}' resolves first: the distinct {relation.Navigation.TargetKey.Name} of the " +
                          $"'{unitOfWork.CollectionNameOf(relation.Navigation.Target)}' documents that match is read (one aggregation, " +
                          $"$match then $group on the key, inside the transaction's session when one is open), and the read takes " +
                          $"{relation.Navigation.OwnerKey.Name} {(relation.Negated ? "$nin" : "$in")} those keys; past " +
                          $"{MongoRelations.MaxInKeys} keys or {MongoRelations.MaxInBytes / 1024} KiB of them, a $lookup of the related " +
                          "documents in the read's own aggregation instead.");
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

    // ---------------------------------------------------------------- text without regard to case

    /// <summary>
    ///     Normalizes the expression, rewrites its case-insensitive text tests into regular expressions, and its ordered
    ///     comparisons of an enum stored as labels into an <c>$in</c> over the labels whose member satisfies them, since the
    ///     server would compare the labels' text where C# compares the members' values.
    /// </summary>
    internal static Expression Rewrite(Expression expression) =>
        EnumOrder.RewriteComparisons(new CaseInsensitiveText().Visit(PredicateNormalizer.Normalize(expression)), MongoModeling.IsLabelEnum);

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
            Folding(folded) is { } folding ? Test(folding.Member, value, TextMatch.Equals, folding.Upper, equalityOperator: true) : null;

        /// <summary>The regular expression test of a member against a value, or <c>null</c> when they are not a member and a value.</summary>
        private static Expression? Test(Expression member, Expression value, TextMatch match, bool? fold, bool equalityOperator = false)
        {
            if (!Reads(member) || !Constant<string>(value, out var text))
            {
                return null;
            }

            if (text is null)
            {
                // Read as the engines read a test in memory (PredicateNormalizer.CompileResidual): a member equal to null is
                // its null test, and so is its folded text compared with == (null folds to null); a folded text's Equals(null)
                // is false (a null receiver fails a text test, and a text is never null); a match against null is refused,
                // as C# refuses it, rather than left to the driver.
                return match switch
                {
                    TextMatch.Equals when fold is null || equalityOperator => Expression.Equal(member, Expression.Constant(null, typeof(string))),
                    TextMatch.Equals => Expression.Constant(false),
                    _ => throw new NotSupportedException($"A text match of '{member}' needs a string value; '{match}' was given null."),
                };
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
