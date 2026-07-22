using System;
using System.Linq;
using System.Linq.Expressions;
using eQuantic.Core.Domain.Entities;

namespace eQuantic.Core.Data.Repository;

/// <summary>
///     The write conventions every provider honours for entities implementing the
///     <c>eQuantic.Core.Domain</c> lifecycle interfaces — no configuration, no hooks to wire:
///     <list type="bullet">
///         <item><see cref="IEntityTimeMark" /> — <c>CreatedAt</c> is stamped on insert (when not already set).</item>
///         <item><see cref="IEntityTimeTrack" /> — <c>UpdatedAt</c> is stamped on every update, including set-based ones.</item>
///         <item>
///             <see cref="IEntityTimeEnded" /> — <c>Remove</c>/<c>DeleteMany</c> become <b>soft deletes</b>
///             (<c>DeletedAt</c> is stamped, the row survives) and every read and set-based write is scoped to
///             live rows; <c>IgnoringQueryFilters()</c> opts a read out, exactly like a global filter.
///         </item>
///     </list>
/// </summary>
public static class EntityLifecycle
{
    /// <summary>Stamps <c>CreatedAt</c> for an insert when the entity tracks it and no explicit value was set.</summary>
    /// <param name="entity">The staged entity.</param>
    public static void StampForInsert(object entity)
    {
        if (entity is IEntityTimeMark marked && marked.CreatedAt == default)
        {
            marked.CreatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>Stamps <c>UpdatedAt</c> for an update when the entity tracks it.</summary>
    /// <param name="entity">The staged entity.</param>
    public static void StampForUpdate(object entity)
    {
        if (entity is IEntityTimeTrack tracked)
        {
            tracked.UpdatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    ///     Turns a delete into a <b>soft delete</b> when the entity supports it: <c>DeletedAt</c> is stamped and
    ///     the caller stages an update instead of a delete.
    /// </summary>
    /// <param name="entity">The staged entity.</param>
    /// <returns>Whether the entity was soft-deleted (and must be staged as an update).</returns>
    public static bool TrySoftDelete(object entity)
    {
        if (entity is not IEntityTimeEnded ended)
        {
            return false;
        }

        ended.DeletedAt = DateTime.UtcNow;
        return true;
    }

    /// <summary>Whether the entity type soft-deletes (implements <see cref="IEntityTimeEnded" />).</summary>
    /// <param name="entityType">The entity type.</param>
    public static bool IsSoftDelete(Type entityType) => typeof(IEntityTimeEnded).IsAssignableFrom(entityType);

    /// <summary>Whether the entity type stamps <c>UpdatedAt</c> (implements <see cref="IEntityTimeTrack" />).</summary>
    /// <param name="entityType">The entity type.</param>
    public static bool IsTimeTracked(Type entityType) => typeof(IEntityTimeTrack).IsAssignableFrom(entityType);

    /// <summary>The live-rows filter (<c>x =&gt; x.DeletedAt == null</c>) for a soft-delete entity, or <c>null</c>.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    public static Expression<Func<TEntity, bool>>? SoftDeleteFilter<TEntity>()
        where TEntity : class =>
        (Expression<Func<TEntity, bool>>?)SoftDeleteFilter(typeof(TEntity));

    /// <summary>The live-rows filter for a soft-delete entity type, or <c>null</c> — the runtime-typed path.</summary>
    /// <param name="entityType">The entity type.</param>
    public static LambdaExpression? SoftDeleteFilter(Type entityType)
    {
        if (!IsSoftDelete(entityType))
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
    ///     The set-based soft-delete factory (<c>x =&gt; new TEntity { DeletedAt = now }</c>) — a provider routes
    ///     <c>DeleteMany</c> through its own <c>UpdateMany</c> machinery with it.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    public static Expression<Func<TEntity, TEntity>> SoftDeleteUpdate<TEntity>()
        where TEntity : class
    {
        var parameter = Expression.Parameter(typeof(TEntity), "x");
        var body = Expression.MemberInit(
            Expression.New(typeof(TEntity)),
            Expression.Bind(
                typeof(TEntity).GetProperty(nameof(IEntityTimeEnded.DeletedAt))!,
                Expression.Constant((DateTime?)DateTime.UtcNow, typeof(DateTime?))));
        return Expression.Lambda<Func<TEntity, TEntity>>(body, parameter);
    }

    /// <summary>
    ///     The <c>UpdatedAt</c> stamp as a set-based assignment, appended by providers to <c>UpdateMany</c>
    ///     translations when the entity tracks updates and the caller did not assign it explicitly.
    /// </summary>
    /// <param name="entityType">The entity type.</param>
    public static Query.SetAssignment? UpdateStamp(Type entityType) =>
        IsTimeTracked(entityType)
            ? new Query.SetAssignment(entityType.GetProperty(nameof(IEntityTimeTrack.UpdatedAt))!, DateTime.UtcNow)
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

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
