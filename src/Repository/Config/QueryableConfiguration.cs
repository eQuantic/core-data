using System;
using System.Linq;

namespace eQuantic.Core.Data.Repository.Config;

public class QueryableConfiguration<TEntity> : Configuration<QueryableConfiguration<TEntity>, TEntity>
    where TEntity : class, IEntity, new()
{
    public Func<IQueryable<TEntity>, IQueryable<TEntity>> BeforeCustomization { get; private set; } = set => set;
    public Func<IQueryable<TEntity>, IQueryable<TEntity>> AfterCustomization { get; private set; } = set => set;
    public bool IgnoreQueryFilters { get; private set; }
    public string SqlRaw { get; private set; }
    public QueryableConfiguration<TEntity> WithBeforeCustomization(Func<IQueryable<TEntity>, IQueryable<TEntity>> customize)
    {
        BeforeCustomization = customize ?? throw new ArgumentNullException(nameof(customize));
        return this;
    }
    
    public QueryableConfiguration<TEntity> WithAfterCustomization(Func<IQueryable<TEntity>, IQueryable<TEntity>> customize)
    {
        AfterCustomization = customize ?? throw new ArgumentNullException(nameof(customize));
        return this;
    }

    public QueryableConfiguration<TEntity> WithoutQueryFilters()
    {
        IgnoreQueryFilters = true;
        return this;
    }

    public QueryableConfiguration<TEntity> FromSqlRaw(string sql)
    {
        SqlRaw = sql;
        return this;
    }
}
