using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Repository.Write;

/// <summary>
///     A write repository that writes "this row, whether or not it exists yet" and "these rows, except the ones
///     already there" as one call that cannot lose a race, through each store's own mechanism: <c>INSERT … ON
///     CONFLICT</c> on PostgreSQL, <c>MERGE … WITH (HOLDLOCK)</c> on SQL Server, a locking read in a transaction on
///     MySQL and MariaDB, <c>findOneAndUpdate</c> with <c>upsert</c> on MongoDB, an ETag-guarded create or replace on
///     Cosmos DB, and lightweight transactions on Cassandra. Every provider's repository implements it: cast to it
///     (<c>(IUpsertRepository&lt;Secret, Guid&gt;)repo</c>) or resolve it from DI.
///     <para>
///         These writes run at once (they are not staged for the next commit), and join an explicit transaction
///         when one is open.
///     </para>
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
public interface IUpsertRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    /// <summary>
    ///     Inserts the entity when no row has its key, and otherwise updates that row with the entity's mapped
    ///     non-key members, in one atomic step; returns the row as the store holds it afterwards, the members the store
    ///     assigns included. The insert takes the store's values for store-generated members and the insert's
    ///     lifecycle stamps; the update never writes store-generated members, keeps the creation stamps and takes the
    ///     update's.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stored row.</returns>
    Task<TEntity> UpsertAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Inserts the entity when no row matches it on the members <paramref name="on" /> names (the key, or members
    ///     the store holds unique, such as <c>x =&gt; new { x.WorkspaceId, x.Name }</c>), and otherwise updates that
    ///     row, in one atomic step; returns the row as the store holds it afterwards. The update applies
    ///     <paramref name="update" /> to the existing row (<c>x =&gt; new Secret { Value = "b", Uses = x.Uses + 1 }</c>),
    ///     or writes the entity's mapped non-key members when it is <c>null</c>; the existing row keeps its key.
    ///     <para>
    ///         Two upserts on the same target at the same time leave one row, and neither fails on that target's
    ///         uniqueness — provided the store holds the target unique (a unique index or constraint, the primary
    ///         key, a Cosmos DB unique key). A key or unique key other than the target that the insert would take
    ///         fails with <see cref="DuplicateKeyException" />.
    ///     </para>
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <param name="on">The members that identify the target row: one member, or an anonymous type of several.</param>
    /// <param name="update">The set-based update applied to an existing row, or <c>null</c> to write the entity.</param>
    /// <param name="conflictFilter">
    ///     Restricts the target to the rows it matches, as a partial unique index does (<c>x =&gt; x.DeletedAt == null</c>
    ///     for an index over the rows not soft-deleted), or <c>null</c>. On PostgreSQL it is the arbiter's predicate,
    ///     and has to imply the partial index's own.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stored row.</returns>
    Task<TEntity> UpsertAsync(TEntity entity, Expression<Func<TEntity, object?>> on,
        Expression<Func<TEntity, TEntity>>? update = null, Expression<Func<TEntity, bool>>? conflictFilter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Inserts a batch at once and returns how many rows it inserted. With <paramref name="skipDuplicates" />, an
    ///     entity whose key or unique key is already taken (by a stored row, or by an earlier entity of the batch) is
    ///     left out without failing; without it, a taken key fails the call with <see cref="DuplicateKeyException" />.
    ///     Lifecycle stamps apply as they do to a staged insert; the values the store assigns are not read back onto
    ///     the entities.
    /// </summary>
    /// <param name="items">The entities to insert.</param>
    /// <param name="skipDuplicates">Whether entities whose key or unique key is taken are left out.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of rows inserted.</returns>
    Task<long> InsertManyAsync(IEnumerable<TEntity> items, bool skipDuplicates = false,
        CancellationToken cancellationToken = default);
}
