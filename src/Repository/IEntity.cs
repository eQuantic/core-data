using System;

namespace eQuantic.Core.Data.Repository
{
    public interface IEntity
    { }

    public interface IEntity<TKey> : IEntity
    {
        TKey GetKey();
    }
}