using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>
///     Whether the search indexes a model declares case-insensitive really are, read from the cluster's own schema. A
///     text test that ignores case pushes down as a <c>LIKE</c> only on an index that matches without regard to case, and
///     the model saying so is not enough: a declaration turned case-insensitive on an index that already exists changes
///     nothing on the cluster until the index is rebuilt (the migration says so), and a <c>LIKE</c> on the old index would
///     miss every row whose case differs, silently. So a read takes the declaration only where
///     <c>system_schema.indexes</c> holds a SASI index on the column, the one the model expects, with the non-tokenizing
///     analyzer, <c>case_sensitive: false</c> and a mode that serves the declared one; anywhere else the test runs
///     client-side, behind <c>.AllowClientEvaluation()</c>, where C# decides.
///     <para>
///         An index found to ignore case is remembered for the session; one that does not is read again by the next read
///         that needs it, so a rebuild is seen without a restart.
///     </para>
/// </summary>
internal static class CassandraSearchIndexes
{
    private static readonly ConditionalWeakTable<ISession, ConcurrentDictionary<Type, CassandraEntityConfiguration>> Verified = new();

    /// <summary>
    ///     The configuration as a read renders it: <paramref name="configuration" /> itself when it declares no
    ///     case-insensitive index or the cluster holds each one as declared, otherwise a copy that keeps the declaration
    ///     only where the cluster does.
    /// </summary>
    /// <param name="session">The session, bound to the keyspace.</param>
    /// <param name="configuration">The entity's mapping.</param>
    /// <param name="cancellationToken">Cancels the read of the schema.</param>
    public static async Task<CassandraEntityConfiguration> VerifiedAsync(ISession session, CassandraEntityConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.SearchColumns.Any(search => search.CaseInsensitive))
        {
            return configuration;
        }

        var verified = Verified.GetOrCreateValue(session);
        if (verified.TryGetValue(configuration.EntityType, out var known))
        {
            return known;
        }

        var live = await CaseInsensitiveColumnsAsync(session, configuration, cancellationToken).ConfigureAwait(false);
        if (configuration.SearchColumns.All(search => !search.CaseInsensitive || live.Contains(search.Column)))
        {
            verified[configuration.EntityType] = configuration;
            return configuration;
        }

        return configuration.WithSearchColumns(configuration.SearchColumns
            .Select(search => search.CaseInsensitive && !live.Contains(search.Column) ? search with { CaseInsensitive = false } : search)
            .ToList());
    }

    /// <summary>The columns of the entity's table whose SASI index the cluster holds case-insensitive, serving the declared mode.</summary>
    private static async Task<HashSet<string>> CaseInsensitiveColumnsAsync(ISession session, CassandraEntityConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var rows = await session.ExecuteAsync(new SimpleStatement(
                "SELECT index_name, options FROM system_schema.indexes WHERE keyspace_name = ? AND table_name = ?",
                session.Keyspace, configuration.TableName.ToLowerInvariant()))
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var options = row.GetValue<IDictionary<string, string>>("options") ?? new Dictionary<string, string>();
            if (!options.TryGetValue("target", out var target)
                || configuration.SearchColumns.FirstOrDefault(search => search.CaseInsensitive && CassandraEntityConfiguration.Same(search.Column, target)) is not { } declared)
            {
                continue;
            }

            var sasi = options.TryGetValue("class_name", out var className) && className.EndsWith("SASIIndex", StringComparison.Ordinal);
            var folds = options.TryGetValue("case_sensitive", out var caseSensitive) && string.Equals(caseSensitive, "false", StringComparison.OrdinalIgnoreCase)
                        && options.TryGetValue("analyzer_class", out var analyzer) && analyzer.EndsWith("NonTokenizingAnalyzer", StringComparison.Ordinal);
            var serves = declared.Mode == CassandraSearchMode.Prefix
                         || (options.TryGetValue("mode", out var mode) && string.Equals(mode, "CONTAINS", StringComparison.OrdinalIgnoreCase));
            if (sasi && folds && serves)
            {
                columns.Add(declared.Column);
            }
        }

        return columns;
    }
}
