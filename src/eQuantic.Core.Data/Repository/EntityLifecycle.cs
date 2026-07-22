using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using eQuantic.Core.Domain.Entities;

namespace eQuantic.Core.Data.Repository;

/// <summary>
///     The write conventions every provider honours for entities implementing the
///     <c>eQuantic.Core.Domain</c> lifecycle interfaces, tuned by <see cref="DataConventions" />:
///     <list type="bullet">
///         <item><see cref="IEntityTimeMark" /> — <c>CreatedAt</c> is stamped on insert (when not already set).</item>
///         <item><see cref="IEntityTimeTrack" /> — <c>UpdatedAt</c> is stamped on every update, including set-based ones.</item>
///         <item>
///             <see cref="IEntityTimeEnded" /> — <c>Remove</c>/<c>DeleteMany</c> become <b>soft deletes</b>
///             (<c>DeletedAt</c> is stamped, the row survives) and every read and set-based write is scoped to
///             live rows; <c>IgnoringQueryFilters()</c> opts a read out, exactly like a global filter.
///         </item>
///         <item>
///             With <see cref="DataConventions.CurrentUserId" /> configured, the <b>who</b> stamps too:
///             <c>CreatedById</c>/<c>UpdatedById</c>/<c>DeletedById</c> members (the
///             <c>eQuantic.Core.DataModel</c> shapes) are set by property-name convention.
///         </item>
///     </list>
///     <b>Provider SPI</b>: providers invoke these conventions at their staging and filter points; application
///     code never needs to call it.
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public static class EntityLifecycle
{
    private static readonly ConcurrentDictionary<(Type Type, string Name), PropertyInfo?> UserProperties = new();

    /// <summary>Stamps <c>CreatedAt</c> (and <c>CreatedById</c>) for an insert, honouring explicit values.</summary>
    /// <param name="entity">The staged entity.</param>
    /// <param name="conventions">The active conventions.</param>
    /// <param name="services">The scope's service provider (handed to the current-user accessor).</param>
    public static void StampForInsert(object entity, DataConventions conventions, IServiceProvider services)
    {
        if (!conventions.LifecycleStamps)
        {
            return;
        }

        if (entity is IEntityTimeMark marked && marked.CreatedAt == default)
        {
            marked.CreatedAt = conventions.Clock.GetUtcNow().UtcDateTime;
        }

        StampUser(entity, "CreatedById", conventions, services, onlyWhenDefault: true);
    }

    /// <summary>Stamps <c>UpdatedAt</c> (and <c>UpdatedById</c>) for an update.</summary>
    /// <param name="entity">The staged entity.</param>
    /// <param name="conventions">The active conventions.</param>
    /// <param name="services">The scope's service provider (handed to the current-user accessor).</param>
    public static void StampForUpdate(object entity, DataConventions conventions, IServiceProvider services)
    {
        if (!conventions.LifecycleStamps)
        {
            return;
        }

        if (entity is IEntityTimeTrack tracked)
        {
            tracked.UpdatedAt = conventions.Clock.GetUtcNow().UtcDateTime;
        }

        StampUser(entity, "UpdatedById", conventions, services, onlyWhenDefault: false);
    }

    /// <summary>
    ///     Turns a delete into a <b>soft delete</b> when the entity supports it and the convention is on:
    ///     <c>DeletedAt</c> (and <c>DeletedById</c>) are stamped and the caller stages an update instead.
    /// </summary>
    /// <param name="entity">The staged entity.</param>
    /// <param name="conventions">The active conventions.</param>
    /// <param name="services">The scope's service provider (handed to the current-user accessor).</param>
    /// <returns>Whether the entity was soft-deleted (and must be staged as an update).</returns>
    public static bool TrySoftDelete(object entity, DataConventions conventions, IServiceProvider services)
    {
        if (!conventions.SoftDelete || entity is not IEntityTimeEnded ended)
        {
            return false;
        }

        ended.DeletedAt = conventions.Clock.GetUtcNow().UtcDateTime;
        StampUser(entity, "DeletedById", conventions, services, onlyWhenDefault: false);
        return true;
    }

    /// <summary>Whether the entity type soft-deletes under the active conventions.</summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="conventions">The active conventions.</param>
    public static bool IsSoftDelete(Type entityType, DataConventions conventions) =>
        conventions.SoftDelete && typeof(IEntityTimeEnded).IsAssignableFrom(entityType);

    /// <summary>The live-rows filter (<c>x =&gt; x.DeletedAt == null</c>) for a soft-delete entity, or <c>null</c>.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="conventions">The active conventions.</param>
    public static Expression<Func<TEntity, bool>>? SoftDeleteFilter<TEntity>(DataConventions conventions)
        where TEntity : class =>
        (Expression<Func<TEntity, bool>>?)SoftDeleteFilter(typeof(TEntity), conventions);

    /// <summary>The live-rows filter for a soft-delete entity type, or <c>null</c> — the runtime-typed path.</summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="conventions">The active conventions.</param>
    public static LambdaExpression? SoftDeleteFilter(Type entityType, DataConventions conventions)
    {
        if (!IsSoftDelete(entityType, conventions))
        {
            return null;
        }

        var parameter = Expression.Parameter(entityType, "x");
        var body = Expression.Equal(
            Expression.Property(parameter, nameof(IEntityTimeEnded.DeletedAt)),
            Expression.Constant(null, typeof(DateTime?)));
        return Expression.Lambda(typeof(Func<,>).MakeGenericType(entityType, typeof(bool)), body, parameter);
    }

    /// <summary>
    ///     The set-based soft-delete factory (<c>x =&gt; new TEntity { DeletedAt = now, DeletedById = user }</c>)
    ///     — a provider routes <c>DeleteMany</c> through its own <c>UpdateMany</c> machinery with it.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="conventions">The active conventions.</param>
    /// <param name="services">The scope's service provider (handed to the current-user accessor).</param>
    public static Expression<Func<TEntity, TEntity>> SoftDeleteUpdate<TEntity>(DataConventions conventions, IServiceProvider services)
        where TEntity : class
    {
        var parameter = Expression.Parameter(typeof(TEntity), "x");
        var bindings = new System.Collections.Generic.List<MemberBinding>
        {
            Expression.Bind(
                typeof(TEntity).GetProperty(nameof(IEntityTimeEnded.DeletedAt))!,
                Expression.Constant((DateTime?)conventions.Clock.GetUtcNow().UtcDateTime, typeof(DateTime?))),
        };

        if (UserValueFor(typeof(TEntity), "DeletedById", conventions, services) is var (property, value) && property is not null)
        {
            bindings.Add(Expression.Bind(property, Expression.Constant(value, property.PropertyType)));
        }

        return Expression.Lambda<Func<TEntity, TEntity>>(
            Expression.MemberInit(Expression.New(typeof(TEntity)), bindings), parameter);
    }

    /// <summary>
    ///     The <c>UpdatedAt</c> stamp as a set-based assignment, appended by providers to <c>UpdateMany</c>
    ///     translations when the entity tracks updates and the caller did not assign it explicitly.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="conventions">The active conventions.</param>
    public static Query.SetAssignment? UpdateStamp(Type entityType, DataConventions conventions) =>
        conventions.LifecycleStamps && typeof(IEntityTimeTrack).IsAssignableFrom(entityType)
            ? new Query.SetAssignment(
                entityType.GetProperty(nameof(IEntityTimeTrack.UpdatedAt))!,
                conventions.Clock.GetUtcNow().UtcDateTime)
            : null;

    /// <summary>Combines two predicates with <c>AndAlso</c> (either may be <c>null</c>).</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="first">The first predicate, or <c>null</c>.</param>
    /// <param name="second">The second predicate, or <c>null</c>.</param>
    public static Expression<Func<TEntity, bool>>? And<TEntity>(
        Expression<Func<TEntity, bool>>? first, Expression<Func<TEntity, bool>>? second)
        where TEntity : class =>
        (Expression<Func<TEntity, bool>>?)And((LambdaExpression?)first, second);

    /// <summary>Combines two predicates with <c>AndAlso</c> — the runtime-typed path.</summary>
    /// <param name="first">The first predicate, or <c>null</c>.</param>
    /// <param name="second">The second predicate, or <c>null</c>.</param>
    public static LambdaExpression? And(LambdaExpression? first, LambdaExpression? second)
    {
        if (first is null || second is null)
        {
            return first ?? second;
        }

        var parameter = first.Parameters[0];
        var body = Expression.AndAlso(first.Body, new ParameterReplacer(second.Parameters[0], parameter).Visit(second.Body));
        return Expression.Lambda(first.Type, body, parameter);
    }

    /// <summary>Sets a <c>…ById</c> member by property-name convention, converting the accessor's value to its type.</summary>
    private static void StampUser(object entity, string property, DataConventions conventions, IServiceProvider services, bool onlyWhenDefault)
    {
        var (target, value) = UserValueFor(entity.GetType(), property, conventions, services);
        if (target is null)
        {
            return;
        }

        if (onlyWhenDefault)
        {
            var current = target.GetValue(entity);
            var untouched = current is null || Equals(current, target.PropertyType.IsValueType
                ? Activator.CreateInstance(target.PropertyType)
                : null);
            if (!untouched)
            {
                return;
            }
        }

        target.SetValue(entity, value);
    }

    private static (PropertyInfo? Property, object? Value) UserValueFor(Type entityType, string property,
        DataConventions conventions, IServiceProvider services)
    {
        if (!conventions.LifecycleStamps || conventions.CurrentUserId is null)
        {
            return (null, null);
        }

        var target = UserProperties.GetOrAdd((entityType, property), static key =>
            key.Type.GetProperty(key.Name, BindingFlags.Public | BindingFlags.Instance) is { CanWrite: true } found
                ? found
                : null);
        if (target is null)
        {
            return (null, null);
        }

        var value = conventions.CurrentUserId(services);
        if (value is null)
        {
            return (null, null);
        }

        var type = Nullable.GetUnderlyingType(target.PropertyType) ?? target.PropertyType;
        if (!type.IsInstanceOfType(value))
        {
            value = type.IsEnum ? Enum.ToObject(type, value) : Convert.ChangeType(value, type);
        }

        return (target, value);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
