using System;
using System.Linq;

namespace eQuantic.Core.Data.Repository.Config;

public class QueryableConfiguration<TEntity> where TEntity : class, IEntity, new()
{
    public Func<ISet<TEntity>, IQueryable<TEntity>> Customize { get; private set; } = set => set;
    
    public QueryableConfiguration<TEntity> WithSetCustomization(Func<ISet<TEntity>, IQueryable<TEntity>> customize)
    {
        Customize = customize ?? throw new ArgumentNullException(nameof(customize));
        return this;
    }
}