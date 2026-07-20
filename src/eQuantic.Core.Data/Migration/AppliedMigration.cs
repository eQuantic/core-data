using System;

namespace eQuantic.Core.Data.Migration;

/// <summary>
/// A migration that has been applied, as recorded in the <see cref="IMigrationHistory" />.
/// </summary>
/// <param name="Id">The stable identifier (see <see cref="MigrationAttribute.Id" />).</param>
/// <param name="Title">The human-readable title from <see cref="MigrationAttribute" />.</param>
/// <param name="Date">The ordering timestamp from <see cref="MigrationAttribute" />.</param>
/// <param name="AppliedAt">When the migration was applied.</param>
public sealed record AppliedMigration(string Id, string Title, DateTime Date, DateTime AppliedAt);
