using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.DataModel;

public abstract class EntityDataBase : EntityDataBase<int>
{
    /// <summary>
    /// Implicit operator to compare entity with id
    /// </summary>
    /// <param name="entity"></param>
    /// <returns></returns>
    public static implicit operator int(EntityDataBase entity)
    {
        return entity.Id;
    }
}

public abstract class EntityDataBase<TKey> : IEntity<TKey>
{
    /// <summary>
    /// Gets or sets the value of the id
    /// </summary>
    [Key]
    [Column(Order = 1)]
    public TKey Id { get; set; } = default!;

    /// <summary>
    /// Gets the key
    /// </summary>
    /// <returns>The id</returns>
    public TKey GetKey()
    {
        return Id;
    }

    /// <summary>
    /// Sets the key
    /// </summary>
    /// <param name="key"></param>
    public void SetKey(TKey key)
    {
        Id = key;
    }
}