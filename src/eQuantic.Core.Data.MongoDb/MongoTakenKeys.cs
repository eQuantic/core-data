using System.Globalization;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>
///     Finds, inside an explicit transaction, which documents of a batch would take a key that is already taken, so
///     that an insert skipping duplicates can leave them out before it writes: MongoDB aborts a transaction on any write
///     error, a duplicate key included, so inside one the duplicates cannot be discarded after the fact as an unordered
///     insert discards them outside it.
///     <para>
///         The keys are the collection's <c>_id</c> and every unique index <c>listIndexes</c> reports (read outside the
///         session: the command is not allowed inside a transaction, and an index is not transactional data). A sparse
///         index holds only the documents carrying one of its fields, a partial index only the documents its
///         <c>partialFilterExpression</c> matches (evaluated here for the operators MongoDB allows in one), and an index
///         with a collation compares its strings under it: the stored documents are read with that collation, inside the
///         session, and the batch's own strings are compared with the platform's culture comparison at the collation's
///         strength. A document is kept when none of its keys is taken by a stored document or by a document kept before
///         it in the batch.
///     </para>
/// </summary>
internal static class MongoTakenKeys
{
    private const int QueryChunk = 500;

    /// <summary>The documents of the batch whose keys are free, in order: the stored ones read inside the session.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="collection">The collection the batch is for.</param>
    /// <param name="session">The session of the open transaction.</param>
    /// <param name="entities">The batch, with its store-generated values assigned.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static async Task<IReadOnlyList<TEntity>> FreeAsync<TEntity>(IMongoCollection<TEntity> collection,
        IClientSessionHandle session, IReadOnlyList<TEntity> entities, CancellationToken cancellationToken)
    {
        var serializer = collection.DocumentSerializer;
        AssignIds(collection, serializer, entities);
        var documents = entities.Select(entity => Render(serializer, entity)).ToList();
        var indexes = await UniqueIndexesAsync(collection, cancellationToken).ConfigureAwait(false);
        var stored = collection.Database.GetCollection<BsonDocument>(collection.CollectionNamespace.CollectionName);

        var keys = new List<string?[]>(documents.Count);
        foreach (var document in documents)
        {
            keys.Add(indexes.Select(index => index.KeyOf(document)).ToArray());
        }

        var taken = new List<HashSet<string>>(indexes.Count);
        for (var position = 0; position < indexes.Count; position++)
        {
            taken.Add(await indexes[position].TakenAsync(stored, session,
                documents.Where((_, row) => keys[row][position] is not null).ToList(), cancellationToken).ConfigureAwait(false));
        }

        // In order: a document is kept when every key it has is free, and the keys it takes are no longer free.
        var free = new List<TEntity>(entities.Count);
        for (var row = 0; row < entities.Count; row++)
        {
            var key = keys[row];
            if (Enumerable.Range(0, indexes.Count).Any(position => key[position] is { } value && taken[position].Contains(value)))
            {
                continue;
            }

            for (var position = 0; position < indexes.Count; position++)
            {
                if (key[position] is { } value)
                {
                    taken[position].Add(value);
                }
            }

            free.Add(entities[row]);
        }

        return free;
    }

    /// <summary>
    ///     Gives every document without an id the one the driver would generate on insert (its id generator's), so it can
    ///     be compared, or filtered on, before it is written.
    /// </summary>
    internal static void AssignIds<TEntity>(IMongoCollection<TEntity> collection, IBsonSerializer<TEntity> serializer, IReadOnlyList<TEntity> entities)
    {
        if (serializer is not IBsonIdProvider provider)
        {
            return;
        }

        foreach (var entity in entities)
        {
            if (provider.GetDocumentId(entity!, out var id, out _, out var generator) && generator is not null && generator.IsEmpty(id))
            {
                provider.SetDocumentId(entity!, generator.GenerateId(collection, entity!));
            }
        }
    }

    private static BsonDocument Render<TEntity>(IBsonSerializer<TEntity> serializer, TEntity entity)
    {
        var document = new BsonDocument();
        using var writer = new BsonDocumentWriter(document);
        serializer.Serialize(BsonSerializationContext.CreateRoot(writer), entity);
        return document;
    }

    private static async Task<IReadOnlyList<UniqueIndex>> UniqueIndexesAsync<TEntity>(IMongoCollection<TEntity> collection,
        CancellationToken cancellationToken)
    {
        // listIndexes is not allowed inside a transaction: the definitions are read outside the session.
        using var cursor = await collection.Indexes.ListAsync(cancellationToken).ConfigureAwait(false);
        var indexes = new List<UniqueIndex>();
        foreach (var definition in await cursor.ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = definition.GetValue("name", BsonNull.Value).ToString()!;
            var unique = name == "_id_" || (definition.TryGetValue("unique", out var flag) && flag.ToBoolean());
            if (!unique || definition.GetValue("key", BsonNull.Value) is not BsonDocument key)
            {
                continue;
            }

            indexes.Add(new UniqueIndex(
                name,
                key.Names.ToList(),
                definition.TryGetValue("sparse", out var sparse) && sparse.ToBoolean(),
                definition.GetValue("partialFilterExpression", BsonNull.Value) as BsonDocument,
                definition.GetValue("collation", BsonNull.Value) as BsonDocument));
        }

        if (indexes.All(index => index.Name != "_id_"))
        {
            // A collection created by the first insert lists no index yet: its _id is unique all the same.
            indexes.Insert(0, new UniqueIndex("_id_", ["_id"], false, null, null));
        }

        return indexes;
    }

    /// <summary>A unique index, and how it holds and compares a document's key.</summary>
    private sealed class UniqueIndex(string name, IReadOnlyList<string> fields, bool sparse, BsonDocument? partial, BsonDocument? collation)
    {
        private readonly KeyComparison _comparison = KeyComparison.For(collation);

        public string Name { get; } = name;

        /// <summary>The document's key under this index, as one comparable string, or <c>null</c> when the index does not hold the document.</summary>
        public string? KeyOf(BsonDocument document)
        {
            var values = fields.Select(field => Get(document, field)).ToList();
            if (sparse && values.All(value => value is null))
            {
                return null;
            }

            if (partial is not null && !PartialFilter.Matches(document, partial))
            {
                return null;
            }

            return Key(document);
        }

        /// <summary>The document's key under this index, as one comparable string, whether or not the index holds it.</summary>
        private string Key(BsonDocument document) =>
            string.Join("\u001f", fields.Select(field => _comparison.Canonical(Get(document, field) ?? BsonNull.Value)));

        /// <summary>The keys of the stored documents that take one of the candidates' keys, read inside the session.</summary>
        public async Task<HashSet<string>> TakenAsync(IMongoCollection<BsonDocument> stored, IClientSessionHandle session,
            IReadOnlyList<BsonDocument> candidates, CancellationToken cancellationToken)
        {
            var taken = new HashSet<string>(StringComparer.Ordinal);
            var projection = new BsonDocument(fields.Select(field => new BsonElement(field, 1)));
            var options = new FindOptions<BsonDocument>
            {
                Projection = projection,
                Collation = collation is null ? null : Collation.FromBsonDocument(Settable(collation)),
            };

            foreach (var chunk in candidates.Chunk(QueryChunk))
            {
                var equalities = new BsonArray(chunk
                    .Select(candidate => new BsonDocument(fields.Select(field => new BsonElement(field, Get(candidate, field) ?? BsonNull.Value))))
                    .DistinctBy(equality => equality.ToJson()));
                var conditions = new BsonArray { new BsonDocument("$or", equalities) };
                if (sparse)
                {
                    conditions.Add(new BsonDocument("$or",
                        new BsonArray(fields.Select(field => new BsonDocument(field, new BsonDocument("$exists", true))))));
                }

                if (partial is not null)
                {
                    conditions.Add(partial);
                }

                using var cursor = await stored.FindAsync(session, new BsonDocument("$and", conditions), options, cancellationToken)
                    .ConfigureAwait(false);
                // The query held the stored documents to the index (sparse, partial); the projection keeps only the key.
                foreach (var document in await cursor.ToListAsync(cancellationToken).ConfigureAwait(false))
                {
                    taken.Add(Key(document));
                }
            }

            return taken;
        }
    }

    /// <summary>A listed index's collation as a query takes it: <c>listIndexes</c> adds the ICU <c>version</c>, which a query does not set.</summary>
    private static BsonDocument Settable(BsonDocument collation)
    {
        var settable = collation.DeepClone().AsBsonDocument;
        settable.Remove("version");
        return settable;
    }

    /// <summary>A value at a dotted path, or <c>null</c> when the document has none there.</summary>
    private static BsonValue? Get(BsonDocument document, string path)
    {
        BsonValue current = document;
        foreach (var segment in path.Split('.'))
        {
            if (current is not BsonDocument level || !level.TryGetValue(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>
    ///     Turns a key's values into a string two keys share exactly when the index holds them equal: numbers by value
    ///     whatever their BSON type, strings as they are or (under a collation) by their sort key at its strength.
    /// </summary>
    private sealed class KeyComparison
    {
        private readonly CompareInfo? _compareInfo;
        private readonly CompareOptions _options;

        private KeyComparison(CompareInfo? compareInfo, CompareOptions options)
        {
            _compareInfo = compareInfo;
            _options = options;
        }

        public static KeyComparison For(BsonDocument? collation)
        {
            var locale = collation?.GetValue("locale", "simple").ToString();
            if (collation is null || locale is null or "simple")
            {
                return new KeyComparison(null, CompareOptions.Ordinal);
            }

            CultureInfo culture;
            try
            {
                culture = CultureInfo.GetCultureInfo(locale.Replace('_', '-'));
            }
            catch (CultureNotFoundException)
            {
                culture = CultureInfo.InvariantCulture;
            }

            var strength = collation.GetValue("strength", 3).ToInt32();
            var options = strength switch
            {
                1 => CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace,
                2 => CompareOptions.IgnoreCase,
                _ => CompareOptions.None,
            };
            return new KeyComparison(culture.CompareInfo, options);
        }

        public string Canonical(BsonValue value) => value.BsonType switch
        {
            BsonType.Null or BsonType.Undefined => "null",
            BsonType.Int32 or BsonType.Int64 => "n:" + value.ToInt64().ToString(CultureInfo.InvariantCulture),
            BsonType.Double => Number(value.AsDouble),
            BsonType.Decimal128 => Number(value.AsDecimal128),
            BsonType.String when _compareInfo is not null =>
                "s:" + Convert.ToBase64String(_compareInfo.GetSortKey(value.AsString, _options).KeyData),
            BsonType.String => "s:" + value.AsString,
            _ => "b:" + value.BsonType + ":" + value.ToJson(),
        };

        // A number is one key whatever its BSON type, as MongoDB's index compares them: 10, 10.0 and 10.00m are one.
        private static string Number(double value) =>
            double.IsFinite(value) && Math.Abs(value) < 7.9e27
                ? Number((decimal)value)
                : "n:" + value.ToString("R", CultureInfo.InvariantCulture);

        private static string Number(Decimal128 value)
        {
            if (Decimal128.IsNaN(value))
            {
                return "n:NaN";
            }

            try
            {
                return Number(Decimal128.ToDecimal(value));
            }
            catch (OverflowException)
            {
                // Past a .NET decimal (or infinite): spelt as the double of the same value is.
                return Number(Decimal128.ToDouble(value));
            }
        }

        /// <summary>The number without trailing fractional zeros: only a fraction's, never an integer's own (10 stays 10).</summary>
        private static string Number(decimal value)
        {
            var text = value.ToString(CultureInfo.InvariantCulture);
            return "n:" + (text.Contains('.') ? text.TrimEnd('0').TrimEnd('.') : text);
        }
    }

    /// <summary>
    ///     Evaluates a <c>partialFilterExpression</c> on a document: the operators MongoDB allows in one (equality,
    ///     <c>$eq</c>, <c>$exists</c>, <c>$gt</c>/<c>$gte</c>/<c>$lt</c>/<c>$lte</c>, <c>$type</c>, <c>$in</c>, <c>$and</c>,
    ///     <c>$or</c>). Any other is refused rather than guessed.
    /// </summary>
    private static class PartialFilter
    {
        public static bool Matches(BsonDocument document, BsonDocument filter)
        {
            foreach (var element in filter)
            {
                var holds = element.Name switch
                {
                    "$and" => element.Value.AsBsonArray.All(part => Matches(document, part.AsBsonDocument)),
                    "$or" => element.Value.AsBsonArray.Any(part => Matches(document, part.AsBsonDocument)),
                    _ => Field(Get(document, element.Name), element.Value),
                };
                if (!holds)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Field(BsonValue? value, BsonValue condition)
        {
            if (condition is not BsonDocument operators || operators.ElementCount == 0 || !operators.GetElement(0).Name.StartsWith('$'))
            {
                return Equal(value, condition);
            }

            foreach (var element in operators)
            {
                var holds = element.Name switch
                {
                    "$eq" => Equal(value, element.Value),
                    "$exists" => (value is not null) == element.Value.ToBoolean(),
                    "$gt" => Compare(value, element.Value) is > 0,
                    "$gte" => Compare(value, element.Value) is >= 0,
                    "$lt" => Compare(value, element.Value) is < 0,
                    "$lte" => Compare(value, element.Value) is <= 0,
                    "$in" => element.Value.AsBsonArray.Any(candidate => Equal(value, candidate)),
                    "$type" => element.Value is BsonArray types ? types.Any(type => IsType(value, type)) : IsType(value, element.Value),
                    _ => throw new NotSupportedException(
                        $"The partial unique index's filter uses '{element.Name}', which MongoDB does not allow in a partialFilterExpression."),
                };
                if (!holds)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Equal(BsonValue? value, BsonValue expected) =>
            expected.IsBsonNull ? value is null || value.IsBsonNull : value is not null && Compare(value, expected) == 0;

        /// <summary>The order of two values of one type bracket, or <c>null</c> when they are of different ones.</summary>
        private static int? Compare(BsonValue? value, BsonValue other)
        {
            if (value is null)
            {
                return null;
            }

            if (value.IsNumeric && other.IsNumeric)
            {
                return value.ToDouble().CompareTo(other.ToDouble());
            }

            return value.BsonType == other.BsonType ? value.CompareTo(other) : null;
        }

        private static bool IsType(BsonValue? value, BsonValue type)
        {
            if (value is null)
            {
                return false;
            }

            if (type.IsNumeric)
            {
                return (int)value.BsonType == type.ToInt32();
            }

            return type.AsString switch
            {
                "number" => value.IsNumeric,
                "double" => value.BsonType == BsonType.Double,
                "string" => value.BsonType == BsonType.String,
                "object" => value.BsonType == BsonType.Document,
                "array" => value.BsonType == BsonType.Array,
                "binData" => value.BsonType == BsonType.Binary,
                "objectId" => value.BsonType == BsonType.ObjectId,
                "bool" => value.BsonType == BsonType.Boolean,
                "date" => value.BsonType == BsonType.DateTime,
                "null" => value.BsonType == BsonType.Null,
                "int" => value.BsonType == BsonType.Int32,
                "long" => value.BsonType == BsonType.Int64,
                "decimal" => value.BsonType == BsonType.Decimal128,
                "timestamp" => value.BsonType == BsonType.Timestamp,
                _ => throw new NotSupportedException($"The partial unique index's filter names the type '{type}', which is not read here."),
            };
        }
    }
}
