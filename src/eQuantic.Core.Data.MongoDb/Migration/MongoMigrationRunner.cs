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

    /// <summary>Initializes the runner.</summary>
    /// <param name="executor">Applies each migration's declared operations.</param>
    /// <param name="history">Tracks which migrations have already run.</param>
    /// <param name="assemblies">The assemblies scanned for migrations.</param>
    public MongoMigrationRunner(IMigrationExecutor executor, IMigrationHistory history, IEnumerable<Assembly> assemblies)
    {
        _executor = executor;
        _history = history;
        _assemblies = assemblies.Distinct().ToArray();
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
        foreach (var (attribute, type) in pending)
        {
            if (applied.Contains(attribute.Id))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var migration = (Data.Migration.Migration)Activator.CreateInstance(type)!;
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

    private List<(MigrationAttribute Attribute, Type Type)> Discover()
    {
        var migrations = _assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(Data.Migration.Migration).IsAssignableFrom(type) && type is { IsAbstract: false, IsClass: true })
            .Select(type => (Attribute: type.GetCustomAttribute<MigrationAttribute>(), Type: type))
            .Where(entry => entry.Attribute is not null)
            .Select(entry => (Attribute: entry.Attribute!, entry.Type))
            .OrderBy(entry => entry.Attribute.Date)
            .ThenBy(entry => entry.Attribute.Id, StringComparer.Ordinal)
            .ToList();

        var duplicate = migrations
            .GroupBy(entry => entry.Attribute.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Two migrations share the id '{duplicate.Key}': {string.Join(", ", duplicate.Select(entry => entry.Type.FullName))}. " +
                "Give them distinct titles or timestamps.");
        }

        return migrations;
    }
}
