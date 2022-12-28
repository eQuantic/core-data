using System;
using System.Threading.Tasks;

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
    Task<int> CommitAndRefreshChangesAsync();

    /// <summary>
    /// Commit all changes made in a container.
    /// </summary>
    ///<remarks>
    /// If the entity have fixed properties and any optimistic concurrency problem exists,
    /// then an exception is thrown
    ///</remarks>
    Task<int> CommitAsync();

    /// <summary>
    /// Gets the entity repository instance
    /// </summary>
    /// <typeparam name="TUnitOfWork">The unit of work</typeparam>
    /// <typeparam name="TEntity">The entity</typeparam>
    /// <typeparam name="TKey">The key of entity</typeparam>
    /// <returns></returns>
    IRepository<TUnitOfWork, TEntity, TKey> GetRepository<TUnitOfWork, TEntity, TKey>() where TEntity : class, IEntity, new() where TUnitOfWork : IUnitOfWork;

    /// <summary>
    /// Gets the entity repository instance
    /// </summary>
    /// <typeparam name="TUnitOfWork">The unit of work</typeparam>
    /// <typeparam name="TEntity">The entity</typeparam>
    /// <typeparam name="TKey">The key of entity</typeparam>
    /// <param name="name"></param>
    /// <returns></returns>
    IRepository<TUnitOfWork, TEntity, TKey> GetRepository<TUnitOfWork, TEntity, TKey>(string name) where TEntity : class, IEntity, new() where TUnitOfWork : IUnitOfWork;

    /// <summary>
    /// Gets the asynchronous entity repository instance
    /// </summary>
    /// <typeparam name="TUnitOfWork">The unit of work</typeparam>
    /// <typeparam name="TEntity">The entity</typeparam>
    /// <typeparam name="TKey">The key of entity</typeparam>
    /// <returns></returns>
    IAsyncRepository<TUnitOfWork, TEntity, TKey> GetAsyncRepository<TUnitOfWork, TEntity, TKey>() where TEntity : class, IEntity, new() where TUnitOfWork : IUnitOfWork;

    /// <summary>
    /// Gets the asynchronous entity repository instance
    /// </summary>
    /// <typeparam name="TUnitOfWork">The unit of work</typeparam>
    /// <typeparam name="TEntity">The entity</typeparam>
    /// <typeparam name="TKey">The key of entity</typeparam>
    /// <param name="name"></param>
    /// <returns></returns>
    IAsyncRepository<TUnitOfWork, TEntity, TKey> GetAsyncRepository<TUnitOfWork, TEntity, TKey>(string name) where TEntity : class, IEntity, new() where TUnitOfWork : IUnitOfWork;
    
    /// <summary>
    /// Rollback tracked changes. See references of UnitOfWork pattern
    /// </summary>
    void RollbackChanges();
}