using System;
using System.Linq;
using eQuantic.Core.Data.Repository.Read;
using eQuantic.Core.Data.Repository.Write;

namespace eQuantic.Core.Data.Repository
{
    /// <summary>
    /// Base interface for implement a "Repository Pattern", for more information about this pattern
    /// see http://martinfowler.com/eaaCatalog/repository.html or http://blogs.msdn.com/adonet/archive/2009/06/16/using-repository-and-unit-of-work-patterns-with-entity-framework-4-0.aspx
    /// </summary>
    /// <remarks>
    /// Indeed, one might think that IDbSet already a generic repository and therefore would not need
    /// this item. Using this interface allows us to ensure PI principle within our domain model
    /// </remarks>
    /// <typeparam name="TEntity">Type of entity for this repository</typeparam>
    /// <typeparam name="TKey">Type of primary key for this entity</typeparam>
    public interface ISpecRepository<TEntity, TKey> : IReadSpecRepository<TEntity, TKey>, IWriteRepository<TEntity, TKey>
        where TEntity : class, IEntity, new()
    {
    }

    /// <summary>
    /// Base interface for implement a "Repository Pattern", for more information about this pattern
    /// see http://martinfowler.com/eaaCatalog/repository.html or http://blogs.msdn.com/adonet/archive/2009/06/16/using-repository-and-unit-of-work-patterns-with-entity-framework-4-0.aspx
    /// </summary>
    /// <remarks>
    /// Indeed, one might think that IDbSet already a generic repository and therefore would not need
    /// this item. Using this interface allows us to ensure PI principle within our domain model
    /// </remarks>
    /// <typeparam name="TEntity">Type of entity for this repository</typeparam>
    /// <typeparam name="TKey">Type of primary key for this entity</typeparam>
    public interface ISpecRepository<TUnitOfWork, TEntity, TKey> : IReadSpecRepository<TUnitOfWork, TEntity, TKey>, IWriteRepository<TUnitOfWork, TEntity, TKey>
        where TUnitOfWork : IUnitOfWork
        where TEntity : class, IEntity, new()
    {
    }
}