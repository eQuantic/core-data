namespace eQuantic.Core.Data.Evolution;

/// <summary>
///     Describes the registered model as a <see cref="ModelSnapshot" />. Each provider implements it over its own
///     model — relational columns, Cassandra partition keys, Cosmos paths — and registers it alongside the model
///     itself, so whatever compares two versions never learns which store it is looking at.
/// </summary>
public interface IModelSnapshotSource
{
    /// <summary>The provider's name, recorded in the snapshot so it is never compared against another store's.</summary>
    string Provider { get; }

    /// <summary>Describes the model as it is now.</summary>
    ModelSnapshot Describe();
}
