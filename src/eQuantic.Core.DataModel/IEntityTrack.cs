using eQuantic.Core.Domain.Entities;

namespace eQuantic.Core.DataModel;

public interface IEntityTrack : IEntityTrack<int>
{
}

public interface IEntityTrack<TUserKey> : IEntityTimeTrack 
    where TUserKey : struct
{
    /// <summary>
    /// Gets or sets the value of the updated by id
    /// </summary>
    TUserKey? UpdatedById { get; set; }
}

public interface IEntityTrack<TUser, TUserKey> : IEntityTrack<TUserKey>
    where TUserKey : struct
{
    /// <summary>
    /// Gets or sets the value of the updated by
    /// </summary>
    TUser? UpdatedBy { get; set; }
}