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
public sealed class RenameFieldOperation(Type entityType, LambdaExpression field, string newName) : MigrationOperation(entityType)
{
    /// <summary>The field selector.</summary>
    public LambdaExpression Field { get; } = field;

    /// <summary>The new field name.</summary>
    public string NewName { get; } = newName;
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
