using System.Collections.Generic;
using System.Linq;

namespace eQuantic.Core.Data.Evolution;

/// <summary>
///     A store described in its own vocabulary — the names and types the database itself uses, not the CLR ones.
///     <para>
///         This is deliberately not a <see cref="ModelSnapshot" />. A snapshot records the model, in CLR terms, so
///         two versions of the model can be compared. This records the database, so what the engine would create
///         can be compared against what is actually there — and the only way that comparison is trustworthy is if
///         both sides are spelled the same way. Producing both is one provider's job for exactly that reason.
///     </para>
/// </summary>
/// <param name="Provider">The store.</param>
/// <param name="Collections">The collections described, in no particular order.</param>
public sealed record DatabaseSnapshot(string Provider, IReadOnlyList<DatabaseCollection> Collections)
{
    /// <summary>The collection stored under the given name, or <c>null</c>.</summary>
    /// <param name="name">The stored name.</param>
    public DatabaseCollection? For(string name) =>
        Collections.FirstOrDefault(collection => collection.Name == name);
}

/// <summary>One table, collection or container.</summary>
/// <param name="Name">The name it is stored under.</param>
/// <param name="EntityType">The CLR type mapped to it, so a finding can name something the reader recognises.</param>
/// <param name="Fields">Its fields.</param>
public sealed record DatabaseCollection(string Name, string EntityType, IReadOnlyList<DatabaseField> Fields)
{
    /// <summary>
    ///     What the store distributes the rows or documents by, in order, where it has such a thing. Empty for the
    ///     relational stores, which do not.
    ///     <para>
    ///         Worth comparing above everything else: a partition key is fixed when the table or container is
    ///         created, so finding a different one means the store cannot be brought to the model at all — no
    ///         migration relocates what is already written. That is a thing to learn from a check rather than from
    ///         a deployment.
    ///     </para>
    /// </summary>
    public IReadOnlyList<string> PartitionKeys { get; init; } = [];

    /// <summary>The field stored under the given name, or <c>null</c>.</summary>
    /// <param name="name">The stored name.</param>
    public DatabaseField? Field(string name) =>
        Fields.FirstOrDefault(field => field.Name == name);
}

/// <summary>One column or field.</summary>
/// <param name="Name">The name it is stored under.</param>
/// <param name="StoredType">Its type, canonicalized so the two sides of a comparison are spelled alike.</param>
/// <param name="Nullable">Whether the store accepts no value for it.</param>
public sealed record DatabaseField(string Name, string StoredType, bool Nullable);
