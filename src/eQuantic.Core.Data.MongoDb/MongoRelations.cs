using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using eQuantic.Core.Data.MongoDb.Repository;
using eQuantic.Core.Data.Query;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using ExpressionVisitor = System.Linq.Expressions.ExpressionVisitor;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>
///     Resolves the tests a filter makes across relations (<see cref="RelationPredicates" />) for one read or write, on
///     the unit of work's session. Each test reads the distinct keys of the related documents that meet its condition
///     first — one aggregation, <c>$match</c> then <c>$group</c> on the key, its target's global filter applied — and
///     becomes an <c>$in</c> over them (<c>$nin</c> negated) when they are at most <see cref="MaxInKeys" /> keys and
///     <see cref="MaxInBytes" /> bytes: the owner's key is then matched through its index. Past either bound the read
///     stops (the keys are never all held), and the test runs in the read's own aggregation instead: a <c>$lookup</c> of
///     one related document meeting the condition, by the owner's key and the related key, whose presence is the test's
///     answer. An <c>$in</c> past 16 MB would not even reach the server, and the <c>$lookup</c> has no such bound; it
///     costs a lookup per owner document, which an index on the related key keeps to one seek each.
///     <para>
///         A set-based write's filter cannot hold a <c>$lookup</c>: past the bound, the documents the filter matches are
///         read first by one aggregation that holds it, with each such relation's answer for each document, and the write
///         runs over them in batches, each filtered on its ids and on the rest of the filter again, every relation taken
///         as it held when it was read. The keys a small relation stands for are read before the write as well, so a
///         write never re-evaluates a relation: a related document written in between is not seen.
///     </para>
///     <para>
///         The reads run asynchronously and observe the cancellation token, or, for a synchronous read, through the
///         driver's synchronous API (every task then completes before it is returned).
///     </para>
/// </summary>
internal sealed class MongoRelations(MongoUnitOfWork unitOfWork, bool synchronous, CancellationToken cancellationToken)
{
    /// <summary>The most related keys a test carries as an <c>$in</c>; past it, the test is a <c>$lookup</c>.</summary>
    public const int MaxInKeys = 10_000;

    /// <summary>The most bytes of related keys a test carries as an <c>$in</c>; past it, the test is a <c>$lookup</c>.</summary>
    public const int MaxInBytes = 1024 * 1024;

    private const string FieldPrefix = "__eq_rel";

    private static readonly HashSet<string> PredicateOperators = new(StringComparer.Ordinal)
    {
        nameof(Queryable.Where), nameof(Queryable.Any), nameof(Queryable.All), nameof(Queryable.Count), nameof(Queryable.LongCount),
        nameof(Queryable.First), nameof(Queryable.FirstOrDefault), nameof(Queryable.Single), nameof(Queryable.SingleOrDefault),
        nameof(Queryable.Last), nameof(Queryable.LastOrDefault),
    };

    private static readonly MethodInfo RelatedRowExists = typeof(RelationPredicates).GetMethod(nameof(RelationPredicates.RelatedRowExists))!;

    private static readonly MethodInfo MqlField = typeof(Mql).GetMethods()
        .Single(method => method.Name == nameof(Mql.Field) && method.GetGenericArguments().Length == 2);

    private static readonly MethodInfo DistinctKeysMethod = typeof(MongoRelations)
        .GetMethod(nameof(DistinctKeysAsync), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo GlobalFilterMethod = typeof(MongoUnitOfWork)
        .GetMethod(nameof(MongoUnitOfWork.GlobalFilter), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private int _fields;

    /// <summary>A predicate with its relations resolved, the stages that must run before it, and the fields they add.</summary>
    internal sealed record Resolved(LambdaExpression Predicate, IReadOnlyList<BsonDocument> Stages, IReadOnlyList<string> Fields);

    // ---------------------------------------------------------------- a whole query

    /// <summary>
    ///     Resolves the relation tests of every predicate a query expression hands its operators, one read at a time (a
    ///     session runs one operation at a time). A <c>Where</c> whose test needs a <c>$lookup</c> gets it right before
    ///     its <c>$match</c>, and the field it adds removed right after.
    /// </summary>
    public async Task<Expression> QueryAsync(Expression query, IQueryProvider provider)
    {
        var calls = new List<(MethodCallExpression Call, int Argument, LambdaExpression Predicate)>();
        new PredicateCalls(calls).Visit(query);
        if (calls.Count == 0)
        {
            return query;
        }

        var plans = new Dictionary<MethodCallExpression, (int Argument, Resolved Resolved)>(ReferenceEqualityComparer.Instance);
        foreach (var (call, argument, predicate) in calls)
        {
            plans[call] = (argument, await PredicateAsync(predicate).ConfigureAwait(false));
        }

        return new PlanApplier(plans, provider).Visit(query);
    }

    // ---------------------------------------------------------------- one predicate

    /// <summary>Resolves a predicate's relation tests: each an <c>$in</c> over the keys read, or a <c>$lookup</c> past the bound.</summary>
    public async Task<Resolved> PredicateAsync(LambdaExpression predicate)
    {
        var marked = RelationPredicates.Mark(predicate, MongoPredicates.Navigation);
        if (ReferenceEquals(marked, predicate))
        {
            return new Resolved(predicate, [], []);
        }

        var root = predicate.Parameters[0];
        var markers = new List<(MethodCallExpression Call, RelationPredicate Relation, Expression OwnerKey)>();
        new MarkerSeeker(markers).Visit(marked.Body);

        var replacements = new Dictionary<MethodCallExpression, Expression>(ReferenceEqualityComparer.Instance);
        var stages = new List<BsonDocument>();
        var fields = new List<string>();
        foreach (var (call, relation, ownerKey) in markers)
        {
            var keys = await KeysAsync(relation).ConfigureAwait(false);
            if (keys is not null)
            {
                replacements[call] = In(relation, ownerKey, keys);
                continue;
            }

            var field = FieldPrefix + _fields++;
            stages.AddRange(await LookupAsync(relation, root, ownerKey, field).ConfigureAwait(false));
            fields.Add(field);
            Expression exists = Expression.Call(MqlField.MakeGenericMethod(root.Type, typeof(bool)), root,
                Expression.Constant(field), Expression.Constant(BooleanSerializer.Instance, typeof(IBsonSerializer<bool>)));
            replacements[call] = relation.Negated ? Expression.Not(exists) : exists;
        }

        var body = new MarkerReplacer(replacements).Visit(marked.Body);
        return new Resolved(Expression.Lambda(predicate.Type, body, predicate.Parameters), stages, fields);
    }

    /// <summary>
    ///     The <c>$lookup</c> of one related document meeting the test's condition (and its target's global filter),
    ///     correlated by the owner's key and the related key, then the field set to whether it found one — never for an
    ///     owner whose key is unset, which no document relates to.
    /// </summary>
    private async Task<IReadOnlyList<BsonDocument>> LookupAsync(RelationPredicate relation, ParameterExpression root,
        Expression ownerKey, string field)
    {
        var navigation = relation.Navigation;
        var local = MongoStages.FieldName(root.Type, Expression.Lambda(ownerKey, root));
        var foreign = MongoStages.ElementName(navigation.Target, navigation.TargetKey.Name) ?? navigation.TargetKey.Name;

        var pipeline = new BsonArray();
        if (Conjunction(GlobalFilter(navigation.Target), relation.Condition) is { } condition)
        {
            // The condition may cross relations of the related entity in turn: they resolve the same way, inside.
            var inner = await PredicateAsync((LambdaExpression)MongoPredicates.Rewrite(condition)).ConfigureAwait(false);
            pipeline.AddRange(inner.Stages);
            pipeline.Add(new BsonDocument("$match", MongoStages.Filter(navigation.Target, inner.Predicate)));
        }

        pipeline.Add(new BsonDocument("$limit", 1));
        pipeline.Add(new BsonDocument("$project", new BsonDocument("_id", 1)));

        return
        [
            new BsonDocument("$lookup", new BsonDocument
            {
                { "from", unitOfWork.CollectionNameOf(navigation.Target) },
                { "localField", local },
                { "foreignField", foreign },
                { "pipeline", pipeline },
                { "as", field },
            }),
            new BsonDocument("$set", new BsonDocument(field, new BsonDocument("$cond", new BsonArray
            {
                new BsonDocument("$eq", new BsonArray { new BsonDocument("$ifNull", new BsonArray { "$" + local, BsonNull.Value }), BsonNull.Value }),
                false,
                new BsonDocument("$gt", new BsonArray { new BsonDocument("$size", "$" + field), 0 }),
            }))),
        ];
    }

    // ---------------------------------------------------------------- the related keys

    /// <summary>The distinct related keys a test stands for, or <c>null</c> when they pass the bound.</summary>
    private Task<IReadOnlyCollection<object?>?> KeysAsync(RelationPredicate relation) =>
        (Task<IReadOnlyCollection<object?>?>)DistinctKeysMethod
            .MakeGenericMethod(relation.Navigation.Target, relation.Navigation.TargetKey.PropertyType)
            .Invoke(this, [relation.Condition, relation.Navigation.TargetKey])!;

    private async Task<IReadOnlyCollection<object?>?> DistinctKeysAsync<TTarget, TMember>(LambdaExpression? condition, PropertyInfo member)
        where TTarget : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var related = Related<TTarget>(condition);
        var expression = await QueryAsync(MongoPredicates.Rewrite(related.Expression), related.Provider).ConfigureAwait(false);
        var ready = ReferenceEquals(expression, related.Expression) ? related : related.Provider.CreateQuery<TTarget>(expression);

        var parameter = Expression.Parameter(typeof(TTarget), "related");
        var selector = Expression.Lambda<Func<TTarget, TMember>>(Expression.Property(parameter, member), parameter);
        // One more than the bound: the server stops there, and finding it says the bound is passed.
        var keys = ready.Select(selector).Distinct().Take(MaxInKeys + 1);

        using var cursor = synchronous
            ? keys.ToCursor(cancellationToken)
            : await keys.ToCursorAsync(cancellationToken).ConfigureAwait(false);
        var read = new List<object?>();
        long bytes = 0;
        while (synchronous ? cursor.MoveNext(cancellationToken) : await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var key in cursor.Current)
            {
                read.Add(key);
                bytes += Size(key);
                if (read.Count > MaxInKeys || bytes > MaxInBytes)
                {
                    return null;
                }
            }
        }

        return read;
    }

    /// <summary>The related documents' query: the target's global filter and the relation's condition, in the open session.</summary>
    private IQueryable<TTarget> Related<TTarget>(LambdaExpression? condition) where TTarget : class
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

    /// <summary>A key's size in an <c>$in</c>, about: its text or bytes, and what BSON spends around each element.</summary>
    private static long Size(object? key) => key switch
    {
        string text => Encoding.UTF8.GetByteCount(text) + 16,
        byte[] bytes => bytes.Length + 16,
        _ => 32,
    };

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

            list.Add(underlying.IsInstanceOfType(key) ? key : Convert.ChangeType(key, underlying, CultureInfo.InvariantCulture));
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

    private LambdaExpression? GlobalFilter(Type entityType) =>
        (LambdaExpression?)GlobalFilterMethod.MakeGenericMethod(entityType).Invoke(unitOfWork, null);

    /// <summary>Both predicates over one parameter (either may be absent).</summary>
    private static LambdaExpression? Conjunction(LambdaExpression? left, LambdaExpression? right)
    {
        if (left is null || right is null)
        {
            return left ?? right;
        }

        var body = new ParameterSwap(right.Parameters[0], left.Parameters[0]).Visit(right.Body);
        return Expression.Lambda(left.Type, Expression.AndAlso(left.Body, body), left.Parameters);
    }

    // ---------------------------------------------------------------- a set-based write

    /// <summary>The filters a set-based write runs in turn (see <see cref="MongoPredicates.WriteFiltersAsync{T}" />).</summary>
    public async Task<IReadOnlyList<FilterDefinition<T>>> WriteFiltersAsync<T>(Expression<Func<T, bool>> filter, IMongoCollection<T> collection)
    {
        var resolved = await PredicateAsync(filter).ConfigureAwait(false);
        var predicate = (Expression<Func<T, bool>>)resolved.Predicate;
        if (resolved.Stages.Count == 0)
        {
            return [Builders<T>.Filter.Where(predicate)];
        }

        // The documents the filter matches, each with what every $lookup answered for it.
        var projection = new BsonDocument("_id", 1);
        foreach (var field in resolved.Fields)
        {
            projection.Add(field, 1);
        }

        var stages = resolved.Stages
            .Append(new BsonDocument("$match", MongoStages.Filter<T>(predicate)))
            .Append(new BsonDocument("$project", projection))
            .ToList();
        var pipeline = PipelineDefinition<T, BsonDocument>.Create(stages);
        var session = unitOfWork.Session;
        var groups = new Dictionary<string, (bool[] Answers, List<BsonValue> Ids)>(StringComparer.Ordinal);
        using (var cursor = session is null
                   ? await collection.AggregateAsync(pipeline, cancellationToken: cancellationToken).ConfigureAwait(false)
                   : await collection.AggregateAsync(session, pipeline, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var document in cursor.Current)
                {
                    var answers = resolved.Fields.Select(field => document.GetValue(field, false).ToBoolean()).ToArray();
                    var key = new string(answers.Select(answer => answer ? '1' : '0').ToArray());
                    if (!groups.TryGetValue(key, out var group))
                    {
                        group = (answers, []);
                        groups[key] = group;
                    }

                    group.Ids.Add(document["_id"]);
                }
            }
        }

        var filters = new List<FilterDefinition<T>>();
        foreach (var (answers, ids) in groups.Values)
        {
            // The rest of the filter is tested again by the write; the relations, as they held when they were read.
            var answered = new FieldAnswers(resolved.Fields, answers).Visit(predicate.Body);
            var rest = new ConstantFolder().Visit(answered);
            if (rest is ConstantExpression { Value: false })
            {
                continue;
            }

            var others = rest is ConstantExpression { Value: true }
                ? null
                : Builders<T>.Filter.Where(Expression.Lambda<Func<T, bool>>(rest, predicate.Parameters));
            foreach (var batch in Batches(ids))
            {
                FilterDefinition<T> byId = new BsonDocumentFilterDefinition<T>(new BsonDocument("_id", new BsonDocument("$in", new BsonArray(batch))));
                filters.Add(others is null ? byId : Builders<T>.Filter.And(byId, others));
            }
        }

        return filters;
    }

    /// <summary>The ids in batches an <c>$in</c> carries: at most <see cref="MaxInKeys" /> ids and <see cref="MaxInBytes" /> bytes each.</summary>
    private static IEnumerable<List<BsonValue>> Batches(List<BsonValue> ids)
    {
        var batch = new List<BsonValue>();
        long bytes = 0;
        foreach (var id in ids)
        {
            var size = id is BsonString text ? Encoding.UTF8.GetByteCount(text.Value) + 16 : 32;
            if (batch.Count > 0 && (batch.Count >= MaxInKeys || bytes + size > MaxInBytes))
            {
                yield return batch;
                batch = [];
                bytes = 0;
            }

            batch.Add(id);
            bytes += size;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    // ---------------------------------------------------------------- visitors

    /// <summary>Collects, innermost first, the query operators whose predicate crosses a relation.</summary>
    private sealed class PredicateCalls(List<(MethodCallExpression Call, int Argument, LambdaExpression Predicate)> found) : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            base.VisitMethodCall(node);
            if (node.Method.DeclaringType?.Name is not ("Queryable" or "MongoQueryable")
                || !PredicateOperators.Contains(node.Method.Name.Replace("Async", string.Empty, StringComparison.Ordinal)))
            {
                return node;
            }

            for (var index = 1; index < node.Arguments.Count; index++)
            {
                if (Strip(node.Arguments[index]) is LambdaExpression { Parameters.Count: 1 } lambda && lambda.ReturnType == typeof(bool)
                    && RelationPredicates.Find(lambda, MongoPredicates.Navigation).Count > 0)
                {
                    found.Add((node, index, lambda));
                }
            }

            return node;
        }
    }

    /// <summary>Puts each resolved predicate in its operator, and a <c>Where</c>'s <c>$lookup</c> stages around it.</summary>
    private sealed class PlanApplier(Dictionary<MethodCallExpression, (int Argument, Resolved Resolved)> plans, IQueryProvider provider)
        : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var visited = (MethodCallExpression)base.VisitMethodCall(node);
            if (!plans.TryGetValue(node, out var plan))
            {
                return visited;
            }

            var arguments = visited.Arguments.ToArray();
            arguments[plan.Argument] = arguments[plan.Argument] is UnaryExpression { NodeType: ExpressionType.Quote }
                ? Expression.Quote(plan.Resolved.Predicate)
                : plan.Resolved.Predicate;
            if (plan.Resolved.Stages.Count == 0)
            {
                return visited.Update(visited.Object, arguments);
            }

            if (visited.Method.Name != nameof(Queryable.Where))
            {
                throw new NotSupportedException(
                    $"'{node.Method.Name}' tests a relation whose related keys are too many for an $in, which needs a $lookup " +
                    "before the test; filter with Where(…) first.");
            }

            var elementType = plan.Resolved.Predicate.Parameters[0].Type;
            arguments[0] = MongoStages.Append(elementType, provider, arguments[0], plan.Resolved.Stages);
            var filtered = visited.Update(visited.Object, arguments);
            return MongoStages.Append(elementType, provider, filtered,
                [new BsonDocument("$unset", new BsonArray(plan.Resolved.Fields))]);
        }
    }

    /// <summary>Finds the markers <see cref="RelationPredicates.Mark(LambdaExpression, Func{Type, PropertyInfo, RelationNavigation})" /> left, in order.</summary>
    private sealed class MarkerSeeker(List<(MethodCallExpression Call, RelationPredicate Relation, Expression OwnerKey)> found) : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method == RelatedRowExists)
            {
                var ownerKey = node.Arguments[0] is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : node.Arguments[0];
                found.Add((node, (RelationPredicate)((ConstantExpression)node.Arguments[1]).Value!, ownerKey));
                return node;
            }

            return base.VisitMethodCall(node);
        }
    }

    /// <summary>Puts each marker's replacement in its place: a negated test's replacement takes the place of its negation too.</summary>
    private sealed class MarkerReplacer(Dictionary<MethodCallExpression, Expression> replacements) : ExpressionVisitor
    {
        protected override Expression VisitUnary(UnaryExpression node) =>
            node is { NodeType: ExpressionType.Not, Operand: MethodCallExpression marker }
            && replacements.TryGetValue(marker, out var replacement)
            && ((RelationPredicate)((ConstantExpression)marker.Arguments[1]).Value!).Negated
                ? replacement
                : base.VisitUnary(node);

        protected override Expression VisitMethodCall(MethodCallExpression node) =>
            replacements.TryGetValue(node, out var replacement) ? replacement : base.VisitMethodCall(node);
    }

    /// <summary>Puts the answer a <c>$lookup</c> gave in place of the test of its field.</summary>
    private sealed class FieldAnswers(IReadOnlyList<string> fields, bool[] answers) : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.IsGenericMethod && node.Method.GetGenericMethodDefinition() == MqlField
                && node.Arguments[1] is ConstantExpression { Value: string field } && IndexOf(fields, field) is var index and >= 0)
            {
                return Expression.Constant(answers[index]);
            }

            return base.VisitMethodCall(node);
        }

        private static int IndexOf(IReadOnlyList<string> fields, string field)
        {
            for (var index = 0; index < fields.Count; index++)
            {
                if (fields[index] == field)
                {
                    return index;
                }
            }

            return -1;
        }
    }

    /// <summary>Folds the boolean constants an answered filter holds (<c>true &amp;&amp; x</c> is <c>x</c>, <c>!false</c> is <c>true</c>).</summary>
    private sealed class ConstantFolder : ExpressionVisitor
    {
        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (node.Type != typeof(bool) || node.NodeType is not (ExpressionType.AndAlso or ExpressionType.OrElse))
            {
                return base.VisitBinary(node);
            }

            var left = Visit(node.Left);
            var right = Visit(node.Right);
            var and = node.NodeType == ExpressionType.AndAlso;
            if (left is ConstantExpression { Value: bool l })
            {
                return l == and ? right : Expression.Constant(l);
            }

            if (right is ConstantExpression { Value: bool r })
            {
                return r == and ? left : Expression.Constant(r);
            }

            return node.Update(left, node.Conversion, right);
        }

        protected override Expression VisitUnary(UnaryExpression node)
        {
            var operand = Visit(node.Operand);
            return node.NodeType == ExpressionType.Not && operand is ConstantExpression { Value: bool value }
                ? Expression.Constant(!value)
                : node.Update(operand);
        }
    }

    private sealed class ParameterSwap(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }

    private static Expression Strip(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Quote } quote)
        {
            expression = quote.Operand;
        }

        return expression;
    }
}
