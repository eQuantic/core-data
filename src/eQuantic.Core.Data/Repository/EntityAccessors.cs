using System;
using System.Collections.Concurrent;
using System.ComponentModel;

namespace eQuantic.Core.Data.Repository;

/// <summary>
///     Reflection-free member access for one entity type — the contract the source generator implements per
///     entity (<c>eQuantic.Core.Data</c> ships the generator inside the package): <see cref="Create" /> is the
///     bare constructor call, <see cref="Get" />/<see cref="Set" /> are direct member reads/writes behind a
///     name switch. Engine SPI: providers consult <see cref="EntityAccessors" /> and fall back to reflection
///     when no accessor is registered, so generation is an optimization, never a requirement.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class EntityAccessor
{
    /// <summary>Creates a new, empty instance.</summary>
    public abstract object Create();

    /// <summary>Reads a member by name (<c>null</c> also when the member is unknown).</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="member">The member name.</param>
    public abstract object? Get(object entity, string member);

    /// <summary>
    ///     Writes a member by name with an already target-typed value (the engine coerces before it assigns,
    ///     exactly as the reflection path does); an unknown member is ignored.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <param name="member">The member name.</param>
    /// <param name="value">The value, already of the member's type.</param>
    public abstract void Set(object entity, string member, object? value);
}

/// <summary>
///     The process-wide registry of generated <see cref="EntityAccessor" />s, filled by module initializers the
///     source generator emits into consuming assemblies. Engine SPI.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class EntityAccessors
{
    private static readonly ConcurrentDictionary<Type, EntityAccessor> Registered = new();

    /// <summary>Registers (or replaces) the accessor for an entity type.</summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="accessor">The generated accessor.</param>
    public static void Register(Type entityType, EntityAccessor accessor) => Registered[entityType] = accessor;

    /// <summary>The registered accessor for an entity type, or <c>null</c> (the caller falls back to reflection).</summary>
    /// <param name="entityType">The entity type.</param>
    public static EntityAccessor? For(Type entityType) =>
        Registered.TryGetValue(entityType, out var accessor) ? accessor : null;
}
