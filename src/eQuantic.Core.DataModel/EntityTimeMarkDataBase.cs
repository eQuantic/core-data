using eQuantic.Core.Domain.Entities;

namespace eQuantic.Core.DataModel;

public abstract class EntityTimeMarkDataBase : EntityDataBase,  IEntityTimeMark
{
    public DateTime CreatedAt { get; set; }
}

public abstract class EntityTimeMarkDataBase<TKey> : EntityDataBase<TKey>,  IEntityTimeMark
{
    public DateTime CreatedAt { get; set; }
}