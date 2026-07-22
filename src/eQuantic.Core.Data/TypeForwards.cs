using System.Runtime.CompilerServices;

// IEntity / IEntity<TKey> moved to eQuantic.Core.DataModel (which now owns the contract and no longer depends
// on this package — the dependency was inverted so the engine can build on the unified entity interfaces).
// Forwarding the types keeps assemblies compiled against eQuantic.Core.Data <= 5.8 binary-compatible: they
// still find eQuantic.Core.Data.Repository.IEntity here, now resolved from DataModel.
[assembly: TypeForwardedTo(typeof(eQuantic.Core.Data.Repository.IEntity))]
[assembly: TypeForwardedTo(typeof(eQuantic.Core.Data.Repository.IEntity<>))]
