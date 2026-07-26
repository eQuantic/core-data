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

    /// <summary>
    ///     Ensures a single-key index exists with rich, provider-interpreted options — a typed partial filter
    ///     (<c>o.Filtered(x =&gt; x.DeletedAt == null)</c>), a structure (<c>o.Gin()</c>, <c>o.Text()</c>), a TTL
    ///     (<c>o.Ttl(...)</c>) — each rejected with guidance by providers that cannot build it.
    /// </summary>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="field">The field selector.</param>
    /// <param name="options">The fluent index options.</param>
    ICollectionMigration<TEntity> Index<TField>(Expression<Func<TEntity, TField>> field, Action<IIndexOptions<TEntity>> options);

    /// <summary>Ensures a composite (multi-key) index exists.</summary>
    /// <param name="keys">The fluent key builder (e.g. <c>k =&gt; k.Descending(x =&gt; x.A).Ascending(x =&gt; x.B)</c>).</param>
    /// <param name="unique">Whether the index enforces uniqueness.</param>
    ICollectionMigration<TEntity> CompositeIndex(Action<IIndexKeyBuilder<TEntity>> keys, bool unique = false);

    /// <summary>
    ///     Adds the stored column for an entity member to an existing table. The member already exists on the
    ///     entity (and therefore in the model); this evolves the live schema to match. Document stores gain
    ///     fields on write, so the operation is a no-op there.
    /// </summary>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="field">The member selector.</param>
    ICollectionMigration<TEntity> AddField<TField>(Expression<Func<TEntity, TField>> field);

    /// <summary>
    ///     Drops a stored column/field by its <b>stored name</b> — the CLR member is usually already gone, so
    ///     the name is a string here by design. On document stores this unsets the field across documents.
    /// </summary>
    /// <param name="field">The stored column/field name.</param>
    ICollectionMigration<TEntity> DropField(string field);

    /// <summary>Converts a field's stored type across existing documents (type/schema evolution).</summary>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="field">The field selector.</param>
    /// <param name="from">The current stored type.</param>
    /// <param name="to">The target stored type.</param>
    ICollectionMigration<TEntity> ConvertField<TField>(Expression<Func<TEntity, TField>> field, MigrationFieldType from, MigrationFieldType to);

    /// <summary>
    ///     Restates a field's stored type to the one the model declares — how a <c>varchar(50)</c> becomes a
    ///     <c>varchar(200)</c>. The size is read from the model, so there is nothing to pass but the field.
    ///     <para>
    ///         A document store has no declared size, so this does nothing there rather than pretending to.
    ///     </para>
    /// </summary>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="field">The field selector.</param>
    ICollectionMigration<TEntity> ResizeField<TField>(Expression<Func<TEntity, TField>> field);

    /// <summary>Renames the collection this entity is stored in.</summary>
    /// <param name="currentName">The name it is stored under today.</param>
    /// <param name="newName">The name it takes.</param>
    ICollectionMigration<TEntity> RenameCollection(string currentName, string newName);

    /// <summary>
    ///     Drops the collection, and everything in it. Nothing checks that this is the one you meant.
    /// </summary>
    /// <param name="name">The stored name of the collection.</param>
    ICollectionMigration<TEntity> DropCollection(string name);

    /// <summary>Renames a field across existing documents.</summary>
    /// <typeparam name="TField">The field type.</typeparam>
    /// <param name="field">The field selector, whose current name is read from the model.</param>
    /// <param name="newName">The new field name.</param>
    ICollectionMigration<TEntity> RenameField<TField>(Expression<Func<TEntity, TField>> field, string newName);

    /// <summary>
    ///     Renames a field, naming both sides outright.
    ///     <para>
    ///         Use this when the model has already moved on — which is always the case for a generated migration.
    ///         The selector overload asks the model what the field is called today, and once the mapping has
    ///         changed that answer is the <em>new</em> name, so the rename would resolve to a no-op while the old
    ///         name stays in the store. Stating both sides describes the transition instead of the destination.
    ///     </para>
    /// </summary>
    /// <param name="currentName">The name the field is stored under today.</param>
    /// <param name="newName">The new field name.</param>
    ICollectionMigration<TEntity> RenameField(string currentName, string newName);

    /// <summary>Applies assignments to the documents matching a predicate (a data migration).</summary>
    /// <param name="predicate">The predicate selecting the documents.</param>
    /// <param name="update">The fluent assignment builder.</param>
    ICollectionMigration<TEntity> Update(Expression<Func<TEntity, bool>> predicate, Action<IUpdateBuilder<TEntity>> update);
}

/// <summary>Fluent options for a single-key index — providers reject the ones they cannot build, with guidance.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public interface IIndexOptions<TEntity> where TEntity : class
{
    /// <summary>Makes the index enforce uniqueness.</summary>
    IIndexOptions<TEntity> Unique();

    /// <summary>Orders the key descending.</summary>
    IIndexOptions<TEntity> Descending();

    /// <summary>Gives the index an explicit name.</summary>
    /// <param name="name">The index name.</param>
    IIndexOptions<TEntity> Named(string name);

    /// <summary>
    ///     Makes the index <b>partial/filtered</b>: only rows matching the typed predicate are indexed
    ///     (PostgreSQL/SQL Server <c>WHERE</c>, MongoDB partial filter). The predicate goes through the same
    ///     interpretation as query filters.
    /// </summary>
    /// <param name="predicate">The typed row predicate.</param>
    IIndexOptions<TEntity> Filtered(Expression<Func<TEntity, bool>> predicate);

    /// <summary>Builds a PostgreSQL <c>GIN</c> index — what makes <c>jsonb</c> and array predicates fast.</summary>
    IIndexOptions<TEntity> Gin();

    /// <summary>Builds a text-search index (MongoDB <c>text</c>).</summary>
    IIndexOptions<TEntity> Text();

    /// <summary>Makes the index a TTL index: documents expire this long after the key's date value (MongoDB).</summary>
    /// <param name="expireAfter">The time to live.</param>
    IIndexOptions<TEntity> Ttl(TimeSpan expireAfter);
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
