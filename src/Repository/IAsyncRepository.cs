using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using eQuantic.Core.Linq;
using eQuantic.Core.Linq.Specification;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Repository
{
    public interface IAsyncRepository : IRepository
    {

    }
    public interface IAsyncRepository<TUnitOfWork> : IAsyncRepository, IRepository<TUnitOfWork>
        where TUnitOfWork : IUnitOfWork
    {
    }

    public interface IAsyncRepository<TEntity, TKey> : IAsyncQueryRepository<TEntity, TKey>, IAsyncCommandRepository<TEntity, TKey>
        where TEntity : class, IEntity, new()
    {
        
    }

    public interface IAsyncRepository<TUnitOfWork, TEntity, TKey> : IAsyncQueryRepository<TUnitOfWork, TEntity, TKey>, IAsyncCommandRepository<TUnitOfWork, TEntity, TKey>
        where TUnitOfWork : IUnitOfWork
        where TEntity : class, IEntity, new()
    { }
}
