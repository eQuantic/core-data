namespace eQuantic.Core.Data.Repository.Sql;

public interface ISqlRepository<TEntity, TKey> : IRepository<IQueryableUnitOfWork, TEntity, TKey> where TEntity : class, IEntity, new()
{
}
