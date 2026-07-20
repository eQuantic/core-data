namespace eQuantic.Core.Data.Repository;

/// <summary>
/// The entity interface
/// </summary>
public interface IEntity
{ }

/// <summary>
/// The entity with key interface
/// </summary>
/// <typeparam name="TKey">The type of the key.</typeparam>
public interface IEntity<TKey> : IEntity
{
    /// <summary>
    /// Gets the key.
    /// </summary>
    /// <returns>The key</returns>
    TKey GetKey();

    /// <summary>
    /// Sets the key.
    /// </summary>
    /// <param name="key"></param>
    void SetKey(TKey key);
}
