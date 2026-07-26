using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace eQuantic.Core.Data.Migration;

/// <summary>A provider-agnostic stored field type, used to describe a type/schema evolution.</summary>
public enum MigrationFieldType
{
    /// <summary>A string.</summary>
    String,

    /// <summary>A boolean.</summary>
    Boolean,

    /// <summary>A 32-bit integer.</summary>
    Int32,

    /// <summary>A 64-bit integer.</summary>
    Int64,

    /// <summary>A double-precision floating-point number.</summary>
    Double,

    /// <summary>A decimal.</summary>
    Decimal,

    /// <summary>A date/time.</summary>
    DateTime,

    /// <summary>A globally unique identifier.</summary>
    Guid,

    /// <summary>A document-store native object identifier (e.g. MongoDB <c>ObjectId</c>).</summary>
    ObjectId
}

/// <summary>
///     A single, provider-agnostic operation declared by a <see cref="Migration" /> through the fluent
///     <see cref="IMigrationBuilder" />. A provider's <see cref="IMigrationExecutor" /> knows how to apply it.
/// </summary>
public abstract class MigrationOperation
{
    /// <summary>The entity type (collection/container) the operation targets.</summary>
    public Type EntityType { get; }

    /// <summary>Initializes the operation for the supplied entity type.</summary>
    /// <param name="entityType">The entity type.</param>
    protected MigrationOperation(Type entityType) => EntityType = entityType;
}

/// <summary>Ensures the collection/container for the entity exists.</summary>
public sealed class EnsureCollectionOperation(Type entityType) : MigrationOperation(entityType);

/// <summary>A single key of an index: a typed member selector and its direction.</summary>
/// <param name="Selector">The member selector (e.g. <c>x =&gt; x.SortingIndex</c>).</param>
/// <param name="Descending">Whether the key is descending.</param>
public sealed record IndexKey(LambdaExpression Selector, bool Descending);

/// <summary>A specialized index structure a provider may support.</summary>
public enum IndexMethod
{
    /// <summary>The store's default index structure (b-tree or equivalent).</summary>
    Default,

    /// <summary>A PostgreSQL <c>GIN</c> index — what makes <c>jsonb</c> and array predicates fast.</summary>
    Gin,

    /// <summary>A text-search index (MongoDB <c>text</c>).</summary>
    Text,
}

/// <summary>Ensures an index (single-key or composite) exists.</summary>
public sealed class EnsureIndexOperation(Type entityType, IReadOnlyList<IndexKey> keys) : MigrationOperation(entityType)
{
    /// <summary>The ordered index keys.</summary>
    public IReadOnlyList<IndexKey> Keys { get; } = keys;

    /// <summary>Whether the index enforces uniqueness.</summary>
    public bool Unique { get; init; }

    /// <summary>When set, a TTL index expiring documents this long after the (single) key's date value.</summary>
    public TimeSpan? ExpireAfter { get; init; }

    /// <summary>An explicit index name, or <c>null</c> to let the provider derive one.</summary>
    public string? Name { get; init; }

    /// <summary>The index structure; providers reject methods they cannot build.</summary>
    public IndexMethod Method { get; init; }

    /// <summary>When set, a partial/filtered index: only rows matching this typed predicate are indexed.</summary>
    public LambdaExpression? Filter { get; init; }
}

/// <summary>Adds the column/field for an entity member to an existing table (document stores gain fields on write).</summary>
public sealed class AddFieldOperation(Type entityType, LambdaExpression field) : MigrationOperation(entityType)
{
    /// <summary>The member selector — the member exists on the entity; the operation adds its stored column.</summary>
    public LambdaExpression Field { get; } = field;
}

/// <summary>
///     Restates a field's stored type to the one the model now declares — how a <c>varchar(50)</c> becomes a
///     <c>varchar(200)</c>.
///     <para>
///         Distinct from <see cref="ConvertFieldOperation" />, which changes what kind of thing is stored and has
///         to rewrite the values. This changes only how much room it has, so the store does the work: a widening
///         is usually free, and a narrowing is the store's to refuse if it would truncate.
///     </para>
/// </summary>
public sealed class ResizeFieldOperation(Type entityType, LambdaExpression field) : MigrationOperation(entityType)
{
    /// <summary>The field selector; the size comes from the model.</summary>
    public LambdaExpression Field { get; } = field;
}

/// <summary>
///     Renames the whole collection — the table, collection or container an entity is stored in.
/// </summary>
public sealed class RenameCollectionOperation(Type entityType, string currentName, string newName)
    : MigrationOperation(entityType)
{
    /// <summary>The name it is stored under today.</summary>
    public string CurrentName { get; } = currentName;

    /// <summary>The name it takes.</summary>
    public string NewName { get; } = newName;
}

/// <summary>
///     Drops the whole collection, and everything in it.
///     <para>
///         Named by its stored name rather than taken from the model, because by the time an entity stops being
///         mapped there is no model entry left to ask. Which also means nothing checks that this is the collection
///         you meant — it is the one operation here that cannot be undone by running something else afterwards.
///     </para>
/// </summary>
public sealed class DropCollectionOperation(Type entityType, string name) : MigrationOperation(entityType)
{
    /// <summary>The stored name of the collection to drop.</summary>
    public string Name { get; } = name;
}

/// <summary>Drops a stored column/field by its <b>stored name</b> (the CLR member is usually already gone).</summary>
public sealed class DropFieldOperation(Type entityType, string field) : MigrationOperation(entityType)
{
    /// <summary>The stored column/field name.</summary>
    public string Field { get; } = field;
}

/// <summary>Converts a field's stored type (schema/type evolution) across existing documents.</summary>
public sealed class ConvertFieldOperation(Type entityType, LambdaExpression field, MigrationFieldType from, MigrationFieldType to)
    : MigrationOperation(entityType)
{
    /// <summary>The field selector.</summary>
    public LambdaExpression Field { get; } = field;

    /// <summary>The current stored type.</summary>
    public MigrationFieldType From { get; } = from;

    /// <summary>The target stored type.</summary>
    public MigrationFieldType To { get; } = to;
}

/// <summary>Renames a field across existing documents.</summary>
public sealed class RenameFieldOperation : MigrationOperation
{
    /// <summary>Initializes the operation, taking the field's current name from the model.</summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="field">The field selector, resolved against the model as it stands.</param>
    /// <param name="newName">The new field name.</param>
    public RenameFieldOperation(Type entityType, LambdaExpression field, string newName) : base(entityType)
    {
        Field = field;
        NewName = newName;
    }

    /// <summary>Initializes the operation with both names stated outright.</summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="currentName">The name the field is stored under today.</param>
    /// <param name="newName">The new field name.</param>
    public RenameFieldOperation(Type entityType, string currentName, string newName) : base(entityType)
    {
        CurrentName = currentName;
        NewName = newName;
    }

    /// <summary>The field selector, when the source is resolved from the model. <c>null</c> when it was stated.</summary>
    public LambdaExpression? Field { get; }

    /// <summary>
    ///     The name the field is stored under today, when stated outright. <c>null</c> when it comes from
    ///     <see cref="Field" /> instead.
    /// </summary>
    public string? CurrentName { get; }

    /// <summary>The new field name.</summary>
    public string NewName { get; }
}

/// <summary>A single assignment applied by an <see cref="UpdateOperation" />.</summary>
/// <param name="Field">The field selector.</param>
/// <param name="Value">The value to set.</param>
public sealed record FieldSet(LambdaExpression Field, object? Value);

/// <summary>Applies a set of field assignments to the documents matching a predicate (a data migration).</summary>
public sealed class UpdateOperation(Type entityType, LambdaExpression predicate, IReadOnlyList<FieldSet> sets)
    : MigrationOperation(entityType)
{
    /// <summary>The predicate selecting the documents to update.</summary>
    public LambdaExpression Predicate { get; } = predicate;

    /// <summary>The assignments to apply.</summary>
    public IReadOnlyList<FieldSet> Sets { get; } = sets;
}

/// <summary>An arbitrary escape-hatch operation with direct access to the provider's execution context.</summary>
public sealed class RunOperation(Func<IMigrationExecutionContext, CancellationToken, Task> action) : MigrationOperation(typeof(object))
{
    /// <summary>The action to run.</summary>
    public Func<IMigrationExecutionContext, CancellationToken, Task> Action { get; } = action;
}
