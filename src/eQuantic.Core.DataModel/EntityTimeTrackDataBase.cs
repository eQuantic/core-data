using eQuantic.Core.Domain.Entities;

namespace eQuantic.Core.DataModel;

public abstract class EntityTimeTrackDataBase : EntityTimeMarkDataBase, IEntityTimeTrack
{
    public DateTime? UpdatedAt { get; set; }
}

public abstract class EntityTimeTrackDataBase<TKey> : EntityTimeMarkDataBase<TKey>, IEntityTimeTrack
{
    public DateTime? UpdatedAt { get; set; }
}