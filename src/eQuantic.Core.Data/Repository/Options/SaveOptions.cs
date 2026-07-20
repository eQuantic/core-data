using System.Diagnostics.CodeAnalysis;

namespace eQuantic.Core.Data.Repository.Options;

/// <summary>
/// Options that control how changes are persisted when a unit of work is committed.
/// </summary>
[ExcludeFromCodeCoverage]
public class SaveOptions
{
    private bool _saveAuditMetadata;
    private object? _userId;

    /// <summary>
    /// Enables audit metadata and records the acting user identifier.
    /// </summary>
    /// <typeparam name="TUserKey">The type of the user identifier.</typeparam>
    /// <param name="userId">The acting user identifier.</param>
    /// <returns>The same <see cref="SaveOptions"/> instance for chaining.</returns>
    public SaveOptions WithAuditMetadata<TUserKey>(TUserKey userId)
    {
        _saveAuditMetadata = true;
        _userId = userId;

        return this;
    }

    /// <summary>
    /// Gets a value indicating whether audit metadata should be saved.
    /// </summary>
    /// <returns><c>true</c> when audit metadata is enabled; otherwise <c>false</c>.</returns>
    public bool IsAuditMetadata() => _saveAuditMetadata;

    /// <summary>
    /// Gets the acting user identifier previously recorded through <see cref="WithAuditMetadata{TUserKey}"/>.
    /// </summary>
    /// <typeparam name="TUserKey">The type of the user identifier.</typeparam>
    /// <returns>The acting user identifier, or the default value when none was recorded.</returns>
    public TUserKey GetUserId<TUserKey>() => _userId is TUserKey userId ? userId : default!;
}
