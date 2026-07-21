using System.Linq.Expressions;
using eQuantic.Core.Data.Query;
using MongoDB.Bson;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>
///     Renders the dialect-agnostic <see cref="UpdateAssignment" /> model (produced by the core
///     <see cref="UpdateInterpreter" />) into a MongoDB update document: constant assignments become <c>$set</c>,
///     numeric read-modify-writes become <c>$inc</c>/<c>$mul</c>, and collection updates become
///     <c>$push</c> (with <c>$each</c>/<c>$position</c>), <c>$addToSet</c> or <c>$pullAll</c> — all atomic on the
///     server. Stored names and values are resolved through the class map (via <see cref="MongoFieldNames" />).
/// </summary>
internal static class MongoUpdate
{
    public static UpdateDefinition<TEntity> Build<TEntity>(Expression<Func<TEntity, TEntity>> updateFactory)
    {
        var update = new BsonDocument();

        foreach (var assignment in UpdateInterpreter.Interpret(updateFactory))
        {
            var field = MongoFieldNames.Resolve(typeof(TEntity), assignment.Member);
            switch (assignment)
            {
                case SetAssignment set:
                    Operator(update, "$set")[field] = MongoFieldNames.Serialize(typeof(TEntity), set.Member, set.Value);
                    break;
                case IncrementAssignment increment:
                    Operator(update, "$inc")[field] = MongoFieldNames.Serialize(typeof(TEntity), increment.Member, increment.Delta);
                    break;
                case MultiplyAssignment multiply:
                    Operator(update, "$mul")[field] = MongoFieldNames.Serialize(typeof(TEntity), multiply.Member, multiply.Factor);
                    break;
                case CollectionAddAssignment add:
                {
                    var items = (BsonArray)MongoFieldNames.Serialize(typeof(TEntity), add.Member, add.ToTypedCollection());
                    var each = new BsonDocument("$each", items);
                    if (add.Prepend)
                    {
                        each.Add("$position", 0);
                    }

                    Operator(update, add.Unique ? "$addToSet" : "$push")[field] = each;
                    break;
                }
                case CollectionRemoveAssignment remove:
                    Operator(update, "$pullAll")[field] =
                        (BsonArray)MongoFieldNames.Serialize(typeof(TEntity), remove.Member, remove.ToTypedCollection());
                    break;
                default:
                    throw new NotSupportedException($"The MongoDB provider cannot render the assignment '{assignment.GetType().Name}'.");
            }
        }

        return new BsonDocumentUpdateDefinition<TEntity>(update);
    }

    private static BsonDocument Operator(BsonDocument update, string name)
    {
        if (!update.TryGetValue(name, out var existing))
        {
            existing = new BsonDocument();
            update[name] = existing;
        }

        return existing.AsBsonDocument;
    }
}
