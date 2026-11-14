using System;

namespace eQuantic.Core.Data.Repository;

/// <summary>
///     Thrown when the store refuses a write because a key or a unique key it holds is already taken: a staged
///     insert (or update) at commit, an upsert that lands on another unique key, <c>InsertManyAsync</c> without
///     skipping, a set-based update, or a bulk load. The same exception on every engine, so a caller tells "that
///     key is taken" from any other failure without knowing a driver's error codes. The driver's exception is the
///     <see cref="Exception.InnerException" />.
///     <para>
///         Cassandra enforces no uniqueness but its primary key, and its plain insert overwrites by definition: there
///         the refusals are those of its lightweight transactions (<c>INSERT … IF NOT EXISTS</c>), as
///         <c>InsertManyAsync</c> runs them.
///     </para>
/// </summary>
public sealed class DuplicateKeyException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="constraintName">The constraint or index the store named, or <c>null</c> when it named none.</param>
    /// <param name="innerException">The driver's exception, or <c>null</c> when the refusal is not one.</param>
    public DuplicateKeyException(string? constraintName, Exception? innerException)
        : this(constraintName is null
                ? "The store refused the write: a key or a unique key it holds is already taken."
                : $"The store refused the write: the key or unique key '{constraintName}' is already taken.",
            constraintName, innerException)
    {
    }

    /// <summary>Initializes the exception with its own message.</summary>
    /// <param name="message">The message.</param>
    /// <param name="constraintName">The constraint or index the store named, or <c>null</c> when it named none.</param>
    /// <param name="innerException">The driver's exception, or <c>null</c> when the refusal is not one.</param>
    public DuplicateKeyException(string message, string? constraintName, Exception? innerException)
        : base(message, innerException)
    {
        ConstraintName = constraintName;
    }

    /// <summary>
    ///     The name of the constraint or index whose key was taken (<c>ix_users_email</c>, a primary key's name), or
    ///     <c>null</c> when the store does not report one (Cosmos DB, Cassandra).
    /// </summary>
    public string? ConstraintName { get; }
}
