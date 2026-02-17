# eQuantic Core Data Library

The **eQuantic Data Core** provides a robust implementation of the **Repository Pattern**, supporting both synchronous and asynchronous operations.

## Version 4.3.0

### Key Features and Improvements (v4.3.0)

- **Resolved Ambiguous Invocation Error**: Refactored `IReadRepository` and `IAsyncReadRepository` by introducing non-configuration-dependent base interfaces (`IReadRepository<TEntity, TKey>` and `IAsyncReadRepository<TEntity, TKey>`). This centralizes methods like `Count`, `CountAsync`, etc., ensuring clear method resolution when multiple repository interfaces are inherited.
- **Improved ExpressionConverter**: Enhanced reflection-based method lookup for EF Core providers (SqlServer, PostgreSql, MySql) for better robustness and .NET 10 compatibility.
- **Dependency Updates**: Updated `eQuantic.Core` to 1.8.4 and `eQuantic.Linq` to 2.1.0.

## Installation

To install **eQuantic.Core.Data**, run the following command in the [Package Manager Console](https://docs.nuget.org/docs/start-here/using-the-package-manager-console):

```powershell
Install-Package eQuantic.Core.Data
```

## Usage Examples

The following are examples of implementing the repository pattern:

- [Repository Pattern Implementation](Repository.md)
