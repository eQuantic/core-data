using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Repository
{
    public interface ISet<TEntity> : IQueryable<TEntity> where TEntity : class, IEntity, new()
    {
        void Delete(Expression<Func<TEntity, bool>> filter);

        Task DeleteAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default);

        long DeleteMany(Expression<Func<TEntity, bool>> filter);

        Task<long> DeleteManyAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default);

        IEnumerable<TEntity> Execute();

        TEntity Find<TKey>(TKey key);

        Task<TEntity> FindAsync<TKey>(TKey key, CancellationToken cancellationToken = default);

        void Insert(TEntity item);

        Task InsertAsync(TEntity item, CancellationToken cancellationToken = default);

        long Update(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity>> updateExpression);

        Task<long> UpdateAsync(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity>> updateExpression, CancellationToken cancellationToken = default);

        long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity>> updateExpression);

        Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity>> updateExpression, CancellationToken cancellationToken = default);
    }
}