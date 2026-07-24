using eQuantic.Core.Domain.Entities;

namespace eQuantic.Core.DataModel;

public interface IEntityOwned : IEntityOwned<int>
{
}

public interface IEntityOwned<TUserKey> : IEntityTimeMark
{
    /// <summary>
    /// Gets or sets the value of the created by id
    /// </summary>
    TUserKey CreatedById { get; set; }
}

public interface IEntityOwned<TUser, TUserKey> : IEntityOwned<TUserKey>
{
    /// <summary>
    /// Gets or sets the value of the created by
    /// </summary>
    TUser? CreatedBy { get; set; }
}