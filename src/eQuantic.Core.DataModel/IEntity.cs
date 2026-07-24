using eQuantic.Core.Domain.Entities;

// The namespace is preserved from where this contract used to live (eQuantic.Core.Data) so the move is
// source-compatible for every consumer; eQuantic.Core.Data forwards the type for binary compatibility.
namespace eQuantic.Core.Data.Repository;

/// <summary>
/// The entity interface
/// </summary>
public interface IEntity
{
}

/// <summary>
/// The entity with key interface. Unifies the data-layer contract with the domain contract
/// (<see cref="IDomainEntity{TKey}"/>): the key accessors come from the domain interface, so a single entity
/// satisfies both worlds.
/// </summary>
/// <typeparam name="TKey">The type of the key.</typeparam>
public interface IEntity<TKey> : IEntity, IDomainEntity<TKey>
{
}
