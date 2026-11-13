using System.Linq.Expressions;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>
///     The aggregation stages the read shapes add to a pipeline where LINQ has no word for them: a sort that places
///     nulls against MongoDB's own order (nulls and missing fields sort as the smallest value), a <c>$lookup</c> with
///     a pipeline for a shaped include, and the <c>$lookup</c> + <c>$count</c> of a relation count. Members render
///     through the driver's own serializers, so a renamed member sorts and filters by its stored name.
/// </summary>
internal static class MongoStages
{
    private const string NullFlag = "__eq_null";

    /// <summary>The stored field a member selector names (<c>x =&gt; x.Position</c> → <c>"Position"</c>, or its rename).</summary>
    public static string FieldName<T>(LambdaExpression selector) =>
        new ExpressionFieldDefinition<T>(Unconverted(selector)).Render(Args<T>()).FieldName;

    /// <summary>Renders a predicate over <typeparamref name="T" /> as a <c>$match</c> filter document.</summary>
    public static BsonDocument Filter<T>(LambdaExpression predicate) =>
        Builders<T>.Filter.Where((Expression<Func<T, bool>>)predicate).Render(Args<T>());

    /// <summary>Whether every sort places nulls where MongoDB already puts them (first ascending, last descending).</summary>
    public static bool NaturalNulls(IReadOnlyList<MemorySort> sorts) =>
        sorts.All(sort => ReadShapes.IsNullsSmallest(sort.Nulls, sort.Descending));

    /// <summary>
    ///     The stages ordering by <paramref name="sorts" />: a plain <c>$sort</c> when nulls go where MongoDB puts them,
    ///     otherwise a flag per such key (<c>{ $in: [{ $type: "$f" }, ["null", "missing"]] }</c>) sorted before the key,
    ///     then removed.
    /// </summary>
    public static IReadOnlyList<BsonDocument> Sort<T>(IReadOnlyList<MemorySort> sorts)
    {
        var flags = new BsonDocument();
        var order = new BsonDocument();
        for (var index = 0; index < sorts.Count; index++)
        {
            var sort = sorts[index];
            var field = FieldName<T>(sort.Key);
            if (!ReadShapes.IsNullsSmallest(sort.Nulls, sort.Descending))
            {
                var flag = NullFlag + index;
                flags.Add(flag, new BsonDocument("$cond", new BsonArray
                {
                    new BsonDocument("$in", new BsonArray { new BsonDocument("$type", "$" + field), new BsonArray { "null", "missing" } }),
                    1,
                    0,
                }));

                // The flag is 1 for a null: first sorts it descending, last ascending.
                order.Add(flag, sort.Nulls == NullsOrder.First ? -1 : 1);
            }

            if (!order.Contains(field))
            {
                order.Add(field, sort.Descending ? -1 : 1);
            }
        }

        if (flags.ElementCount == 0)
        {
            return [new BsonDocument("$sort", order)];
        }

        return
        [
            new BsonDocument("$addFields", flags),
            new BsonDocument("$sort", order),
            new BsonDocument("$project", new BsonDocument(flags.Names.Select(name => new BsonElement(name, 0)))),
        ];
    }

    /// <summary>Appends raw stages to a LINQ query, keeping its element type (and its serializer).</summary>
    public static IQueryable<T> Append<T>(IQueryable<T> query, IEnumerable<BsonDocument> stages)
    {
        var serializer = BsonSerializer.LookupSerializer<T>();
        foreach (var stage in stages)
        {
            query = query.AppendStage(new BsonDocumentPipelineStageDefinition<T, T>(stage, serializer));
        }

        return query;
    }

    /// <summary>The element name a class-mapped member is stored under, or <c>null</c> when it is not mapped.</summary>
    public static string? ElementName(Type type, string member)
    {
        var classMap = BsonClassMap.LookupClassMap(type);
        if (classMap.IdMemberMap is { } id && string.Equals(id.MemberName, member, StringComparison.Ordinal))
        {
            return "_id";
        }

        return classMap.AllMemberMaps.FirstOrDefault(map => string.Equals(map.MemberName, member, StringComparison.Ordinal))?.ElementName;
    }

    private static RenderArgs<T> Args<T>() => new(BsonSerializer.LookupSerializer<T>(), BsonSerializer.SerializerRegistry);

    private static LambdaExpression Unconverted(LambdaExpression selector)
    {
        var body = selector.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        return ReferenceEquals(body, selector.Body) ? selector : Expression.Lambda(body, selector.Parameters);
    }
}
