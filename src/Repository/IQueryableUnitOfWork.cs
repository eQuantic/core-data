using System;
using System.Linq;

namespace eQuantic.Core.Data.Repository
{
    public interface IQueryableUnitOfWork : IUnitOfWork
    {
        /// <summary>
        /// Returns a IQueryable instance for access to entities of the given type in the context
        /// </summary>
        /// <typeparam name="TEntity"></typeparam>
        /// <returns></returns>
        ISet<TEntity> CreateSet<TEntity>() where TEntity : class, IEntity, new();
    }
}