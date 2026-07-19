# eQuantic Core Data Library

The **eQuantic Data Core** provides a robust implementation of the **Repository Pattern**, supporting both synchronous and asynchronous operations.

## Version 5.0.0

Version 5 is a deliberate, breaking redesign of the repository contracts. See
[docs/CONTRACTS_V5_DESIGN.md](docs/CONTRACTS_V5_DESIGN.md) for the full rationale
and migration guidance.

### Key changes (v5.0.0)

- **One method per operation.** Filtering, sorting, includes and change tracking
  are expressed through a single `QueryOptions<TEntity>` argument instead of the
  previous `Action<Configuration<TEntity>>` callbacks and filter/specification
  overload sets. The read repositories drop from ~100 members to ~23.
- **Real paging.** Paged reads return `PagedResult<T>` (items plus total count
  and page metadata) via a `PageRequest`, instead of a bare `IEnumerable<T>`.
- **Persistence engine out of the signatures.** The `TUnitOfWork` type parameter
  is removed from the repository and unit-of-work interfaces
  (`IRepository<TEntity, TKey>`, `uow.GetRepository<TEntity, TKey>()`).
- **The entity owns its key.** Repositories are constrained with
  `IEntity<TKey>`, and the `new()` constraint is removed.
- **Provider-agnostic contracts.** The SQL/relational surface moves to the
  Entity Framework provider layer.
- **Modern C#.** Nullable reference types and XML documentation are enabled; the
  package builds with zero warnings on net6.0–net10.0.
- **Dependencies.** The unused `eQuantic.Core` reference is removed; `eQuantic.Linq`
  remains at 2.1.0.

## Installation

To install **eQuantic.Core.Data**, run the following command in the [Package Manager Console](https://docs.nuget.org/docs/start-here/using-the-package-manager-console):

```powershell
Install-Package eQuantic.Core.Data
```

## Usage Examples

The following are examples of implementing the repository pattern:

- [Repository Pattern Implementation](Repository.md)
