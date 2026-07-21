using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Core.Data.MongoDb.Repository;
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
/// </summary>
internal static class MongoInclude
{
    private const BindingFlags Members = BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance;

    /// <summary>Applies every include path to the query.</summary>
    public static IQueryable<TEntity> Apply<TEntity>(IQueryable<TEntity> query, MongoUnitOfWork unitOfWork,
        IReadOnlyCollection<string> paths) where TEntity : class
    {
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                query = ApplyPath(query, unitOfWork, path.Trim());
            }
        }

        return query;
    }

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

    private static PropertyInfo? ForeignKey(Type type, string referencedName)
    {
        var byAttribute = type.GetProperties(Members).FirstOrDefault(property =>
            property.GetCustomAttribute<ForeignKeyAttribute>()?.Name.Equals(referencedName, StringComparison.OrdinalIgnoreCase) == true);

        return byAttribute ?? type.GetProperties(Members)
            .FirstOrDefault(property => property.Name.Equals($"{referencedName}Id", StringComparison.OrdinalIgnoreCase));
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
