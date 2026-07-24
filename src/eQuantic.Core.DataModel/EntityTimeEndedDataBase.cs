using eQuantic.Core.Domain.Entities;

namespace eQuantic.Core.DataModel;

public abstract class EntityTimeEndedDataBase : EntityTimeTrackDataBase, IEntityTimeEnded
{
    public DateTime? DeletedAt { get; set; }
}

public abstract class EntityTimeEndedDataBase<TKey> : EntityTimeTrackDataBase<TKey>, IEntityTimeEnded
{
    public DateTime? DeletedAt { get; set; }
}

public abstract class EntityTimeEndedDataBase<TKey, TUserKey> 
    : EntityTimeTrackDataBase<TKey>, IEntityTimeEnded
{
    public DateTime? DeletedAt { get; set; }
}