using System;
using System.Collections.Generic;
using eQuantic.Linq.Sorter;

namespace eQuantic.Core.Data.Repository.Config;

public class Configuration<TEntity>
{
    public HashSet<string> Properties { get; protected set; } = new();
    public string Tag { get; protected set; }
    public bool HasNoTracking { get; protected set; }
    public HashSet<ISorting<TEntity>> SortingColumns { get; protected set; } = new();
}

public class Configuration<TConfig, TEntity> : Configuration<TEntity>
    where TEntity : class, IEntity, new()
    where TConfig : Configuration<TConfig, TEntity>
    
{
    public TConfig WithProperties(params string[] properties)
    {
        if (properties == null)
        {
            throw new ArgumentNullException(nameof(properties));
        }

        Properties.UnionWith(properties);
        return (TConfig)this;
    }

    public TConfig WithSorting(params ISorting<TEntity>[] sortingColumns)
    {
        if (sortingColumns == null)
        {
            throw new ArgumentNullException(nameof(sortingColumns));
        }

        SortingColumns.UnionWith(sortingColumns);
        return (TConfig)this;
    }
    
    public TConfig WithTag(string tag)
    {
        Tag = tag ?? throw new ArgumentNullException(nameof(tag));
        return (TConfig)this;
    }

    public TConfig WithNoTracking()
    {
        HasNoTracking = true;
        return (TConfig)this;
    }
}

public class DefaultConfiguration<TEntity> : Configuration<DefaultConfiguration<TEntity>, TEntity>
    where TEntity : class, IEntity, new()
{

}
