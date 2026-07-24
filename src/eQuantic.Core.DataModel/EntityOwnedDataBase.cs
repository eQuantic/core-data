using System.ComponentModel.DataAnnotations.Schema;

namespace eQuantic.Core.DataModel;

public abstract class EntityOwnedDataBase : EntityOwnedDataBase<int>, IEntityOwned
{
}

public abstract class EntityOwnedDataBase<TUserKey> : EntityDataBase, IEntityOwned<TUserKey>
{
    public DateTime CreatedAt { get; set; }
    public TUserKey CreatedById { get; set; } = default!;
}

/// <summary>
/// The entity owned data base class
/// </summary>
/// <seealso cref="EntityDataBase"/>
public abstract class EntityOwnedDataBase<TUser, TUserKey> : EntityDataBase, IEntityOwned<TUser, TUserKey>
{
    /// <summary>
    /// Gets or sets the value of the created by id
    /// </summary>
    public TUserKey CreatedById { get; set; } = default!;

    /// <summary>
    /// Gets or sets the value of the created by
    /// </summary>
    public virtual TUser? CreatedBy { get; set; }
    
    /// <summary>
    /// Gets or sets the value of the created on
    /// </summary>
    [DatabaseGenerated(DatabaseGeneratedOption.Computed)]
    public DateTime CreatedAt { get; set; }
}