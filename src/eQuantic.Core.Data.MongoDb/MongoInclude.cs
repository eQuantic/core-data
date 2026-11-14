using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Core.Data.MongoDb.Repository;
using eQuantic.Core.Data.Query;
using eQuantic.Core.Data.Repository.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>
///     Applies <c>QueryOptions.Include</c> navigation paths as server-side <c>$lookup</c> joins on the aggregation
///     pipeline (through the driver's LINQ <c>GroupJoin</c>). Two navigation shapes are supported:
///     <list type="bullet">
///         <item>
///             <b>reference</b> (<c>x =&gt; x.Customer</c>): the entity holds the foreign key and it is matched to
///             the referenced document's id; the single match populates the navigation.
///         </item>
///         <item>
///             <b>collection</b> (<c>x =&gt; x.Orders</c>): the element documents hold the foreign key back to the
///             entity's id; every match populates the navigation collection.
///         </item>
///     </list>
///     Keys are resolved by <c>[ForeignKey]</c>/<c>[Key]</c>/<c>[BsonId]</c> attributes or by the
///     <c>{Name}Id</c>/<c>Id</c> conventions. The entity is re-projected by the join, so it must expose a
///     parameterless constructor and writable navigation members.
///     <para>
///         A shaped collection include (a filter, an order, a skip or a limit per parent) runs as a <c>$lookup</c>
///         with a pipeline — <c>localField</c>/<c>foreignField</c> correlate it, and the shape's <c>$match</c>,
///         <c>$sort</c>, <c>$skip</c> and <c>$limit</c> run inside it, per parent, on the server (MongoDB 5.0 or
///         later). The joined array lands in the navigation's own element, so the navigation must be class-mapped.
///     </para>
/// </summary>
internal static class MongoInclude
{
    private const BindingFlags Members = BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance;

    /// <summary>Applies every include path to the query.</summary>
    public static IQueryable<TEntity> Apply<TEntity>(IQueryable<TEntity> query, MongoUnitOfWork unitOfWork,
        IReadOnlyCollection<string> paths, IReadOnlyDictionary<string, IncludeShape>? shapes = null) where TEntity : class
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            query = shapes is not null && shapes.TryGetValue(path.Trim(), out var shape)
                ? ApplyShaped(query, unitOfWork, path.Trim(), shape)
                : ApplyPath(query, unitOfWork, path.Trim());
        }

        return query;
    }

    /// <summary>
    ///     Whether a member is a collection of documents of another collection holding a foreign key back to
    ///     <paramref name="parentType" /> — a relation the server counts with a <c>$lookup</c>, never an embedded array.
    /// </summary>
    public static bool IsRelation(Type parentType, PropertyInfo member) =>
        RelationCountInterpreter.ElementType(member.PropertyType) is { IsClass: true } element
        && element != typeof(string)
        && ForeignKey(element, parentType.Name) is not null;

    /// <summary>
    ///     The <c>$lookup</c> of a relation count: the related documents matching the filter, correlated by the foreign
    ///     key, counted inside the lookup (<c>$count</c>) into <paramref name="field" /> — nothing but the count travels.
    ///     The related documents are read from the collection the unit of work keeps them in.
    /// </summary>
    public static BsonDocument CountLookup(MongoUnitOfWork unitOfWork, Type parentType, RelationCount count, string field)
    {
        var foreignKey = ForeignKey(count.ElementType, parentType.Name)
                         ?? throw new InvalidOperationException(
                             $"Cannot count '{count.Navigation.Name}': no foreign key found on '{count.ElementType.Name}' back to '{parentType.Name}'.");
        var pipeline = new BsonArray();
        if (count.Predicate is not null)
        {
            pipeline.Add(new BsonDocument("$match", RenderFilter(count.ElementType, count.Predicate)));
        }

        pipeline.Add(new BsonDocument("$count", "n"));
        return Lookup(unitOfWork, parentType, PrimaryKey(parentType), count.ElementType, foreignKey, pipeline, field);
    }

    private static IQueryable<TEntity> ApplyShaped<TEntity>(IQueryable<TEntity> query, MongoUnitOfWork unitOfWork, string path,
        IncludeShape shape)
        where TEntity : class
    {
        var navigation = typeof(TEntity).GetProperty(path, Members)
                         ?? throw new InvalidOperationException(
                             $"Cannot include '{path}': '{typeof(TEntity).Name}' has no such navigation property.");
        var elementType = ElementType(navigation.PropertyType)
                          ?? throw new NotSupportedException(
                              $"Cannot shape the include '{path}': a filter, an order or a limit applies to a collection, and it is a reference.");
        var foreignKey = ForeignKey(elementType, typeof(TEntity).Name)
                         ?? throw new InvalidOperationException(
                             $"Cannot include '{path}': no foreign key found on '{elementType.Name}' back to '{typeof(TEntity).Name}' " +
                             $"(expected [ForeignKey(\"{typeof(TEntity).Name}\")] or a '{typeof(TEntity).Name}Id' property).");
        var target = MongoStages.ElementName(typeof(TEntity), navigation.Name)
                     ?? throw new NotSupportedException(
                         $"Cannot include '{path}' with a shape: the navigation is not mapped on '{typeof(TEntity).Name}', so the " +
                         "joined documents have no element to land in. Keep the member mapped (and null when written).");

        var pipeline = new BsonArray();
        if (shape.Filter is not null)
        {
            pipeline.Add(new BsonDocument("$match", RenderFilter(elementType, shape.Filter)));
        }

        foreach (var stage in SortStages(elementType, ReadShapes.Sorts(shape)))
        {
            pipeline.Add(stage);
        }

        if (shape.SkipCount is > 0)
        {
            pipeline.Add(new BsonDocument("$skip", shape.SkipCount.Value));
        }

        if (shape.TakeCount is { } take)
        {
            pipeline.Add(new BsonDocument("$limit", take));
        }

        var lookup = Lookup(unitOfWork, typeof(TEntity), PrimaryKey(typeof(TEntity)), elementType, foreignKey, pipeline, target);
        return MongoStages.Append(query, [lookup]);
    }

    /// <summary>A <c>$lookup</c> with a pipeline, from the collection the unit of work keeps the element type in.</summary>
    private static BsonDocument Lookup(MongoUnitOfWork unitOfWork, Type parentType, PropertyInfo localKey, Type elementType,
        PropertyInfo foreignKey, BsonArray pipeline, string target) =>
        new("$lookup", new BsonDocument
        {
            { "from", unitOfWork.CollectionNameOf(elementType) },
            { "localField", MongoStages.ElementName(parentType, localKey.Name) ?? localKey.Name },
            { "foreignField", MongoStages.ElementName(elementType, foreignKey.Name) ?? foreignKey.Name },
            { "pipeline", pipeline },
            { "as", target },
        });

    private static BsonDocument RenderFilter(Type elementType, System.Linq.Expressions.LambdaExpression predicate) =>
        MongoStages.Filter(elementType, predicate);

    private static IReadOnlyList<BsonDocument> SortStages(Type elementType, IReadOnlyList<MemorySort> sorts) =>
        sorts.Count == 0
            ? []
            : (IReadOnlyList<BsonDocument>)typeof(MongoStages).GetMethod(nameof(MongoStages.Sort))!
                .MakeGenericMethod(elementType).Invoke(null, [sorts])!;

    private static IQueryable<TEntity> ApplyPath<TEntity>(IQueryable<TEntity> query, MongoUnitOfWork unitOfWork, string path)
        where TEntity : class
    {
        var navigation = typeof(TEntity).GetProperty(path, Members)
            ?? throw new InvalidOperationException(
                $"Cannot include '{path}': '{typeof(TEntity).Name}' has no such navigation property.");

        if (ElementType(navigation.PropertyType) is { } elementType)
        {
            // collection navigation: entity.Id == element.{Entity}Id
            var primaryKey = PrimaryKey(typeof(TEntity));
            var foreignKey = ForeignKey(elementType, typeof(TEntity).Name)
                ?? throw new InvalidOperationException(
                    $"Cannot include '{path}': no foreign key found on '{elementType.Name}' back to '{typeof(TEntity).Name}' " +
                    $"(expected [ForeignKey(\"{typeof(TEntity).Name}\")] or a '{typeof(TEntity).Name}Id' property).");
            return Dispatch(nameof(Collection), [typeof(TEntity), elementType, primaryKey.PropertyType],
                query, unitOfWork, navigation, primaryKey, foreignKey);
        }

        // reference navigation: entity.{Nav}Id == referenced.Id
        var referencedType = navigation.PropertyType;
        var localKey = ForeignKey(typeof(TEntity), navigation.Name)
            ?? throw new InvalidOperationException(
                $"Cannot include '{path}': no foreign key found on '{typeof(TEntity).Name}' " +
                $"(expected [ForeignKey(\"{navigation.Name}\")] or a '{navigation.Name}Id' property).");
        var referencedKey = PrimaryKey(referencedType);
        return Dispatch(nameof(Reference), [typeof(TEntity), referencedType, localKey.PropertyType],
            query, unitOfWork, navigation, localKey, referencedKey);
    }

    private static IQueryable<TEntity> Dispatch<TEntity>(string method, Type[] typeArguments,
        IQueryable<TEntity> query, MongoUnitOfWork unitOfWork, PropertyInfo navigation, PropertyInfo outerKey, PropertyInfo innerKey)
        where TEntity : class =>
        (IQueryable<TEntity>)typeof(MongoInclude)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeArguments)
            .Invoke(null, [query, unitOfWork, navigation, outerKey, innerKey])!;

    // ---------------------------------------------------------------- typed join cores

    private static IQueryable<TEntity> Reference<TEntity, TRef, TKey>(IQueryable<TEntity> query, MongoUnitOfWork unitOfWork,
        PropertyInfo navigation, PropertyInfo localKey, PropertyInfo referencedKey)
        where TEntity : class where TRef : class
    {
        var foreign = unitOfWork.GetCollection<TRef>().AsQueryable();
        var result = ResultSelector<TEntity, TRef>(navigation, matches => First<TRef>(matches));
        return query.GroupJoin(foreign, Key<TEntity, TKey>(localKey), Key<TRef, TKey>(referencedKey), result);
    }

    private static IQueryable<TEntity> Collection<TEntity, TElement, TKey>(IQueryable<TEntity> query, MongoUnitOfWork unitOfWork,
        PropertyInfo navigation, PropertyInfo localKey, PropertyInfo foreignKey)
        where TEntity : class where TElement : class
    {
        var foreign = unitOfWork.GetCollection<TElement>().AsQueryable();
        var result = ResultSelector<TEntity, TElement>(navigation, matches => Materialize<TElement>(matches, navigation.PropertyType));
        return query.GroupJoin(foreign, Key<TEntity, TKey>(localKey), Key<TElement, TKey>(foreignKey), result);
    }

    // ---------------------------------------------------------------- expression builders

    /// <summary>Builds <c>x =&gt; x.Member</c> as a key selector of the join's key type (converting when needed).</summary>
    private static Expression<Func<T, TKey>> Key<T, TKey>(PropertyInfo member)
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        Expression body = Expression.Property(parameter, member);
        if (member.PropertyType != typeof(TKey))
        {
            body = Expression.Convert(body, typeof(TKey));
        }

        return Expression.Lambda<Func<T, TKey>>(body, parameter);
    }

    /// <summary>
    ///     Builds <c>(entity, matches) =&gt; new TEntity { ...copied members..., Navigation = navValue(matches) }</c> —
    ///     the projection the driver renders as a <c>$project</c> after the <c>$lookup</c>.
    /// </summary>
    private static Expression<Func<TEntity, IEnumerable<TJoined>, TEntity>> ResultSelector<TEntity, TJoined>(
        PropertyInfo navigation, Func<Expression, Expression> navValue)
    {
        var entity = Expression.Parameter(typeof(TEntity), "e");
        var matches = Expression.Parameter(typeof(IEnumerable<TJoined>), "m");

        var bindings = typeof(TEntity)
            .GetProperties(Members)
            .Where(property => property is { CanRead: true, CanWrite: true } && property.Name != navigation.Name)
            .Select(property => (MemberBinding)Expression.Bind(property, Expression.Property(entity, property)))
            .Append(Expression.Bind(navigation, navValue(matches)))
            .ToArray();

        var projection = Expression.MemberInit(Expression.New(typeof(TEntity)), bindings);
        return Expression.Lambda<Func<TEntity, IEnumerable<TJoined>, TEntity>>(projection, entity, matches);
    }

    /// <summary>The joined array's first element (<c>$lookup</c> match) for a reference navigation.</summary>
    private static Expression First<TJoined>(Expression matches) =>
        Expression.Call(typeof(Enumerable), nameof(Enumerable.FirstOrDefault), [typeof(TJoined)], matches);

    /// <summary>The joined array shaped into the collection navigation's declared type (<c>List</c>/array).</summary>
    private static Expression Materialize<TElement>(Expression matches, Type navigationType)
    {
        if (navigationType.IsArray)
        {
            return Expression.Call(typeof(Enumerable), nameof(Enumerable.ToArray), [typeof(TElement)], matches);
        }

        var list = Expression.Call(typeof(Enumerable), nameof(Enumerable.ToList), [typeof(TElement)], matches);
        return navigationType.IsAssignableFrom(typeof(List<TElement>))
            ? list
            : Expression.Convert(list, navigationType);
    }

    // ---------------------------------------------------------------- convention resolution

    private static Type? ElementType(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        return type.GetInterfaces()
            .Append(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    /// <summary>The foreign-key member on <paramref name="type" /> naming <paramref name="referencedName" />: <c>[ForeignKey]</c>, then <c>{Name}Id</c>.</summary>
    internal static PropertyInfo? ForeignKey(Type type, string referencedName)
    {
        var byAttribute = type.GetProperties(Members).FirstOrDefault(property =>
            property.GetCustomAttribute<ForeignKeyAttribute>()?.Name.Equals(referencedName, StringComparison.OrdinalIgnoreCase) == true);

        return byAttribute ?? type.GetProperties(Members)
            .FirstOrDefault(property => property.Name.Equals($"{referencedName}Id", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The primary-key member of a document type, or <c>null</c> when none resolves.</summary>
    internal static PropertyInfo? TryPrimaryKey(Type type)
    {
        try
        {
            return PrimaryKey(type);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static PropertyInfo PrimaryKey(Type type)
    {
        var byAttribute = type.GetProperties(Members).FirstOrDefault(property =>
            property.GetCustomAttribute<BsonIdAttribute>() != null || property.GetCustomAttribute<KeyAttribute>() != null);
        if (byAttribute is not null)
        {
            return byAttribute;
        }

        var byConvention = type.GetProperties(Members).FirstOrDefault(property =>
            property.Name.Equals("Id", StringComparison.OrdinalIgnoreCase));
        if (byConvention is not null)
        {
            return byConvention;
        }

        var idMember = BsonClassMap.LookupClassMap(type).IdMemberMap?.MemberName;
        return (idMember is not null ? type.GetProperty(idMember, Members) : null)
               ?? throw new InvalidOperationException($"'{type.Name}' has no resolvable primary key for an include join.");
    }
}
