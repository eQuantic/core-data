﻿using System;
using eQuantic.Core.Data.Repository.Read;
using eQuantic.Core.Data.Repository.Write;

namespace eQuantic.Core.Data.Repository
{
    /// <summary>
    /// Reposity interface
    /// </summary>
    /// <seealso cref="System.IDisposable" />
    public interface IRepository : IDisposable
    {
    }

    /// <summary>
    /// Repository with Unit Of Work interface
    /// </summary>
    /// <typeparam name="TUnitOfWork">The type of the unit of work.</typeparam>
    /// <seealso cref="System.IDisposable" />
    public interface IRepository<out TUnitOfWork> : IRepository where TUnitOfWork : IUnitOfWork
    {
        /// <summary>
        /// Get the unit of work in this repository
        /// </summary>
        TUnitOfWork UnitOfWork { get; }
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
    public interface IRepository<TEntity, TKey> : IReadRepository<TEntity, TKey>, IWriteRepository<TEntity, TKey>
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
    /// <typeparam name="TUnitOfWork">Type of unit of work</typeparam>
    /// <typeparam name="TEntity">Type of entity for this repository</typeparam>
    /// <typeparam name="TKey">Type of primary key for this entity</typeparam>
    public interface IRepository<TUnitOfWork, TEntity, TKey> : IReadRepository<TUnitOfWork, TEntity, TKey>, IWriteRepository<TUnitOfWork, TEntity, TKey>
        where TUnitOfWork : IUnitOfWork
        where TEntity : class, IEntity, new()
    {
    }
}