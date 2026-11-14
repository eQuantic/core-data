using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using eQuantic.Linq.Specification;

namespace eQuantic.Core.Data.Repository.Write;

/// <summary>
///     A write repository whose set-based update hands back the rows it wrote, as stored after the update — so
///     incrementing a counter and reading its new value is one atomic call: <c>UPDATE … RETURNING</c> on PostgreSQL,
///     <c>UPDATE … OUTPUT INSERTED</c> on SQL Server, a locking read in a transaction on MySQL and MariaDB,
///     <c>findOneAndUpdate</c> per document on MongoDB, a patch conditioned on the filter on Cosmos DB, and a
///     lightweight transaction per row on Cassandra. Every provider's repository implements it: cast to it or resolve
///     it from DI.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
public interface IReturningUpdateRepository<TEntity, TKey>
    where TEntity : class, IEntity<TKey>
{
    /// <summary>
    ///     Applies <paramref name="update" /> to the rows matching <paramref name="filter" />, as <c>UpdateManyAsync</c>
    ///     does (at once, each row only while it still matches, joining an open explicit transaction), and returns
    ///     every row it updated as stored after the update, the members the store assigns included.
    /// </summary>
    /// <param name="filter">The rows to update.</param>
    /// <param name="update">The set-based update (<c>x =&gt; new ItemType { NextSequence = x.NextSequence + 1 }</c>).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated rows.</returns>
    Task<IReadOnlyList<TEntity>> UpdateAndGetAsync(Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TEntity>> update, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Applies <paramref name="update" /> to the rows matching <paramref name="specification" /> and returns every
    ///     row it updated, as stored after the update.
    /// </summary>
    /// <param name="specification">The rows to update.</param>
    /// <param name="update">The set-based update.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The updated rows.</returns>
    Task<IReadOnlyList<TEntity>> UpdateAndGetAsync(ISpecification<TEntity> specification,
        Expression<Func<TEntity, TEntity>> update, CancellationToken cancellationToken = default);
}
