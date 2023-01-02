using System;
using System.Collections.Generic;

namespace eQuantic.Core.Data.Repository.Config;

public class Configuration
{
    public HashSet<string> Properties { get; protected set; }
    public string Tag { get; protected set; }
}

public class Configuration<TConfig, TEntity> : Configuration
    where TEntity : class, IEntity, new()
    where TConfig : Configuration<TConfig, TEntity>
    
{
    public TConfig WithProperties(params string[] properties)
    {
        if (properties == null)
            throw new ArgumentNullException(nameof(properties));
        
        Properties = new HashSet<string>(properties);
        return (TConfig)this;
    }

    public TConfig WithTag(string tag)
    {
        Tag = tag ?? throw new ArgumentNullException(nameof(tag));
        return (TConfig)this;
    }
}