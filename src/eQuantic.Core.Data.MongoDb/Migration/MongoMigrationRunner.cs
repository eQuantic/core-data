using System.Reflection;
using eQuantic.Core.Data.Migration;

namespace eQuantic.Core.Data.MongoDb.Migration;

/// <summary>
///     Discovers the <see cref="Data.Migration.Migration" /> types marked with <see cref="MigrationAttribute" />
///     across the supplied assemblies, orders them by timestamp, skips the ones already recorded in the
///     <see cref="IMigrationHistory" />, and applies the rest in order through the <see cref="IMigrationExecutor" />
///     — recording each as it succeeds. Safe to call on every startup.
/// </summary>
public sealed class MongoMigrationRunner : IMigrationRunner
{
    private readonly IMigrationExecutor _executor;
    private readonly IMigrationHistory _history;
    private readonly IReadOnlyList<Assembly> _assemblies;
    private readonly MigrationSource? _source;

    /// <summary>Initializes the runner.</summary>
    /// <param name="executor">Applies each migration's declared operations.</param>
    /// <param name="history">Tracks which migrations have already run.</param>
    /// <param name="assemblies">The assemblies scanned for migrations.</param>
    public MongoMigrationRunner(IMigrationExecutor executor, IMigrationHistory history, IEnumerable<Assembly> assemblies,
        MigrationSource? source = null)
    {
        _executor = executor;
        _history = history;
        _assemblies = assemblies.Distinct().ToArray();
        _source = source;
    }

    /// <inheritdoc />
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        await _history.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        var pending = Discover();
        if (pending.Count == 0)
        {
            return 0;
        }

        var applied = new HashSet<string>(await _history.GetAppliedIdsAsync(cancellationToken).ConfigureAwait(false));

        var count = 0;
        foreach (var (attribute, migration) in pending)
        {
            if (applied.Contains(attribute.Id))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var builder = new MigrationBuilder();
            migration.Up(builder);

            await _executor.ApplyAsync(builder.Operations, cancellationToken).ConfigureAwait(false);
            await _history
                .RecordAsync(new AppliedMigration(attribute.Id, attribute.Title, attribute.Date, DateTime.UtcNow), cancellationToken)
                .ConfigureAwait(false);

            count++;
        }

        return count;
    }

    private IReadOnlyList<(MigrationAttribute Attribute, Data.Migration.Migration Instance)> Discover() =>
        MigrationDiscovery.Pending(_assemblies, _source);
}
