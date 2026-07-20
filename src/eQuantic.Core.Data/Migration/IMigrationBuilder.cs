using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Migration;

/// <summary>
///     The fluent, typed surface a <see cref="Migration" /> declares its work through — no query strings,
///     no field-name literals. Every operation is recorded and later applied by a provider's
///     <see cref="IMigrationExecutor" />.
/// </summary>
public interface IMigrationBuilder
{
    /// <summary>Declares operations against the <typeparamref name="TEntity" /> collection/container.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="configure">The fluent configuration.</param>
    /// <returns>The same builder for chaining.</returns>
    IMigrationBuilder For<TEntity>(Action<ICollectionMigration<TEntity>> configure) where TEntity : class;

    /// <summary>Declares an arbitrary escape-hatch step with direct provider access.</summary>
    /// <param name="operation">The action to run.</param>
    /// <returns>The same builder for chaining.</returns>
    IMigrationBuilder Run(Func<IMigrationExecutionContext, CancellationToken, Task> operation);

    /// <summary>The operations declared so far, in order.</summary>
    IReadOnlyList<MigrationOperation> Operations { get; }
}

/// <summary>Fluent, typed operations against a single entity's collection/container.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface ICollectionMigration<TEntity> where TEntity : class
{
    /// <summary>Ensures the collection/container exists.</summary>
    ICollectionMigration<TEntity> EnsureCollection();

    /// <summary>Ensures a single-key index exists.</summary>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="field">The field selector (e.g. <c>x =&gt; x.CreatedAt</c>).</param>
    /// <param name="descending">Whether the key is descending.</param>
    /// <param name="unique">Whether the index enforces uniqueness.</param>
    ICollectionMigration<TEntity> Index<TField>(Expression<Func<TEntity, TField>> field, bool descending = false, bool unique = false);

    /// <summary>Ensures a composite (multi-key) index exists.</summary>
    /// <param name="keys">The fluent key builder (e.g. <c>k =&gt; k.Descending(x =&gt; x.A).Ascending(x =&gt; x.B)</c>).</param>
    /// <param name="unique">Whether the index enforces uniqueness.</param>
    ICollectionMigration<TEntity> CompositeIndex(Action<IIndexKeyBuilder<TEntity>> keys, bool unique = false);

    /// <summary>Converts a field's stored type across existing documents (type/schema evolution).</summary>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="field">The field selector.</param>
    /// <param name="from">The current stored type.</param>
    /// <param name="to">The target stored type.</param>
    ICollectionMigration<TEntity> ConvertField<TField>(Expression<Func<TEntity, TField>> field, MigrationFieldType from, MigrationFieldType to);

    /// <summary>Renames a field across existing documents.</summary>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="field">The field selector.</param>
    /// <param name="newName">The new field name.</param>
    ICollectionMigration<TEntity> RenameField<TField>(Expression<Func<TEntity, TField>> field, string newName);

    /// <summary>Applies assignments to the documents matching a predicate (a data migration).</summary>
    /// <param name="predicate">The predicate selecting the documents.</param>
    /// <param name="update">The fluent assignment builder.</param>
    ICollectionMigration<TEntity> Update(Expression<Func<TEntity, bool>> predicate, Action<IUpdateBuilder<TEntity>> update);
}

/// <summary>Builds the ordered keys of a composite index with typed selectors.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface IIndexKeyBuilder<TEntity> where TEntity : class
{
    /// <summary>Appends an ascending key.</summary>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="field">The field selector.</param>
    IIndexKeyBuilder<TEntity> Ascending<TField>(Expression<Func<TEntity, TField>> field);

    /// <summary>Appends a descending key.</summary>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="field">The field selector.</param>
    IIndexKeyBuilder<TEntity> Descending<TField>(Expression<Func<TEntity, TField>> field);
}

/// <summary>Builds the assignments of an <see cref="ICollectionMigration{TEntity}.Update" /> with typed selectors.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface IUpdateBuilder<TEntity> where TEntity : class
{
    /// <summary>Assigns a value to a field.</summary>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="field">The field selector.</param>
    /// <param name="value">The value to set.</param>
    IUpdateBuilder<TEntity> Set<TField>(Expression<Func<TEntity, TField>> field, TField value);
}
