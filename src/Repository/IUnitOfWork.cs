using System;
using System.Threading;
using System.Threading.Tasks;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.Repository;

/// <summary>
/// Contract for ‘UnitOfWork pattern’. For more
/// related info see http://martinfowler.com/eaaCatalog/unitOfWork.html or
/// http://msdn.microsoft.com/en-us/magazine/dd882510.aspx
/// In this solution, the Unit Of Work is implemented using the out-of-box
/// Entity Framework Context (EF 6.0 DbContext) persistence engine. But in order to
/// comply the PI (Persistence Ignorant) principle in our Domain, we implement this interface/contract.
/// This interface/contract should be complied by any UoW implementation to be used with this Domain.
/// </summary>
public interface IUnitOfWork : IDisposable
{
    /// <summary>
    /// Commit all changes made in a container.
    /// </summary>
    ///<remarks>
    /// If the entity have fixed properties and any optimistic concurrency problem exists,
    /// then an exception is thrown
    ///</remarks>
    int Commit();

    /// <summary>
    /// Commit all changes made in a container.
    /// </summary>
    ///<remarks>
    /// If the entity have fixed properties and any optimistic concurrency problem exists,
    /// then an exception is thrown
    ///</remarks>
    int Commit(Action<SaveOptions> options);

    /// <summary>
    /// Commit all changes made in  a container.
    /// </summary>
    ///<remarks>
    /// If the entity have fixed properties and any optimistic concurrency problem exists,
    /// then 'client changes' are refreshed - Client wins
    ///</remarks>
    int CommitAndRefreshChanges();

    /// <summary>
    /// Commit all changes made in  a container.
    /// </summary>
    ///<remarks>
    /// If the entity have fixed properties and any optimistic concurrency problem exists,
    /// then 'client changes' are refreshed - Client wins
    ///</remarks>
    int CommitAndRefreshChanges(Action<SaveOptions> options);

    /// <summary>
    /// Commit all changes made in  a container.
    /// </summary>
    ///<remarks>
    /// If the entity have fixed properties and any optimistic concurrency problem exists,
    /// then 'client changes' are refreshed - Client wins
    ///</remarks>
    Task<int> CommitAndRefreshChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Commit all changes made in  a container.
    /// </summary>
    ///<remarks>
    /// If the entity have fixed properties and any optimistic concurrency problem exists,
    /// then 'client changes' are refreshed - Client wins
    ///</remarks>
    Task<int> CommitAndRefreshChangesAsync(Action<SaveOptions> options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commit all changes made in a container.
    /// </summary>
    ///<remarks>
    /// If the entity have fixed properties and any optimistic concurrency problem exists,
    /// then an exception is thrown
    ///</remarks>
    Task<int> CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Commit all changes made in a container.
    /// </summary>
    ///<remarks>
    /// If the entity have fixed properties and any optimistic concurrency problem exists,
    /// then an exception is thrown
    ///</remarks>
    Task<int> CommitAsync(Action<SaveOptions> options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rollback tracked changes. See references of UnitOfWork pattern
    /// </summary>
    void RollbackChanges();
}

public interface IUnitOfWork<TUnitOfWork> : IUnitOfWork
    where TUnitOfWork : IUnitOfWork
{
    /// <summary>
    /// Gets the entity repository instance
    /// </summary>
    /// <typeparam name="TEntity">The entity</typeparam>
    /// <typeparam name="TKey">The key of entity</typeparam>
    /// <returns></returns>
    IRepository<TUnitOfWork, TEntity, TKey> GetRepository<TEntity, TKey>() where TEntity : class, IEntity, new();

    /// <summary>
    /// Gets the asynchronous entity repository instance
    /// </summary>
    /// <typeparam name="TEntity">The entity</typeparam>
    /// <typeparam name="TKey">The key of entity</typeparam>
    /// <returns></returns>
    IAsyncRepository<TUnitOfWork, TEntity, TKey> GetAsyncRepository<TEntity, TKey>() where TEntity : class, IEntity, new();
}
