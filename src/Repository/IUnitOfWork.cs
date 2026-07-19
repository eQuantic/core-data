using System;
using System.Threading;
using System.Threading.Tasks;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.Repository;

/// <summary>
/// Contract for the 'Unit Of Work' pattern. For more related info see
/// http://martinfowler.com/eaaCatalog/unitOfWork.html. To comply with the
/// Persistence Ignorance principle in the domain, this contract abstracts the
/// underlying persistence engine.
/// </summary>
public interface IUnitOfWork : IDisposable
{
    /// <summary>
    /// Commits all changes made in the container.
    /// </summary>
    /// <returns>The number of affected entries.</returns>
    int Commit();

    /// <summary>
    /// Commits all changes made in the container.
    /// </summary>
    /// <param name="options">The save options.</param>
    /// <returns>The number of affected entries.</returns>
    int Commit(Action<SaveOptions> options);

    /// <summary>
    /// Commits all changes made in the container, refreshing client changes on conflict.
    /// </summary>
    /// <returns>The number of affected entries.</returns>
    int CommitAndRefreshChanges();

    /// <summary>
    /// Commits all changes made in the container, refreshing client changes on conflict.
    /// </summary>
    /// <param name="options">The save options.</param>
    /// <returns>The number of affected entries.</returns>
    int CommitAndRefreshChanges(Action<SaveOptions> options);

    /// <summary>
    /// Commits all changes made in the container, refreshing client changes on conflict.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of affected entries.</returns>
    Task<int> CommitAndRefreshChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits all changes made in the container, refreshing client changes on conflict.
    /// </summary>
    /// <param name="options">The save options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of affected entries.</returns>
    Task<int> CommitAndRefreshChangesAsync(Action<SaveOptions> options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits all changes made in the container.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of affected entries.</returns>
    Task<int> CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits all changes made in the container.
    /// </summary>
    /// <param name="options">The save options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of affected entries.</returns>
    Task<int> CommitAsync(Action<SaveOptions> options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rolls back tracked changes.
    /// </summary>
    void RollbackChanges();

    /// <summary>
    /// Gets the entity repository instance.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TKey">The type of the entity key.</typeparam>
    /// <returns>The repository.</returns>
    IRepository<TEntity, TKey> GetRepository<TEntity, TKey>()
        where TEntity : class, IEntity<TKey>;

    /// <summary>
    /// Gets the asynchronous entity repository instance.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TKey">The type of the entity key.</typeparam>
    /// <returns>The asynchronous repository.</returns>
    IAsyncRepository<TEntity, TKey> GetAsyncRepository<TEntity, TKey>()
        where TEntity : class, IEntity<TKey>;
}
