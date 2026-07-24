namespace eQuantic.Core.DataModel;

public abstract class EntityTrackDataBase : EntityTrackDataBase<int>, IEntityTrack
{
}

public abstract class EntityTrackDataBase<TUserKey> 
    : EntityOwnedDataBase<TUserKey>, IEntityTrack<TUserKey>
    where TUserKey : struct
{
    public DateTime? UpdatedAt { get; set; }
    public TUserKey? UpdatedById { get; set; }
}

/// <summary>
/// The entity track data base class
/// </summary>
/// <seealso cref="EntityDataBase"/>
public abstract class EntityTrackDataBase<TUser, TUserKey> 
    : EntityOwnedDataBase<TUser, TUserKey>, IEntityTrack<TUser, TUserKey>
    where TUserKey : struct
{
    /// <summary>
    /// Gets or sets the value of the updated by id
    /// </summary>
    public TUserKey? UpdatedById { get; set; }
    
    /// <summary>
    /// Gets or sets the value of the updated by
    /// </summary>
    public virtual TUser? UpdatedBy { get; set; }
    
    /// <summary>
    /// Gets or sets the value of the updated on
    /// </summary>
    public DateTime? UpdatedAt { get; set; }
}