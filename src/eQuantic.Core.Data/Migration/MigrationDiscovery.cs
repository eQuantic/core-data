using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;

namespace eQuantic.Core.Data.Migration;

/// <summary>
///     Resolves the migrations a runner should consider, from both sources: the ones registered explicitly on a
///     <see cref="MigrationSource" /> (AOT-safe, no reflection) and the ones discovered by scanning assemblies
///     (the classic convenience). The same migration found both ways counts once; two <b>different</b>
///     migrations sharing an id is a mistake and throws. The result is ordered by timestamp — the order
///     migrations must apply in. Engine SPI: every provider's runner funnels through here, so discovery behaves
///     identically across stores.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class MigrationDiscovery
{
    /// <summary>The migrations to consider, ordered by timestamp then id, one instance per migration id.</summary>
    /// <param name="assemblies">The assemblies to scan (empty for the AOT-safe explicit-only path).</param>
    /// <param name="source">The explicitly registered migrations, or <c>null</c>.</param>
    /// <exception cref="InvalidOperationException">Two different migration types share an id.</exception>
    [RequiresUnreferencedCode("Scanning assemblies for migrations is reflection; register migrations explicitly " +
                             "with a MigrationSource when trimming or publishing NativeAOT.")]
    public static IReadOnlyList<(MigrationAttribute Attribute, Migration Instance)> Pending(
        IEnumerable<Assembly> assemblies, MigrationSource? source)
    {
        // Explicit registrations first — already constructed, so they satisfy an id without any reflection.
        var candidates = (source?.Migrations ?? [])
            .Select(migration => (Attribute: Describe(migration.GetType()), Type: migration.GetType(), Instance: migration))
            .Where(candidate => candidate.Attribute is not null)
            .ToList();

        foreach (var type in assemblies.Distinct().SelectMany(assembly => assembly.GetTypes()))
        {
            if (typeof(Migration).IsAssignableFrom(type) && type is { IsAbstract: false, IsClass: true }
                && Describe(type) is { } attribute)
            {
                candidates.Add((attribute, type, null!));
            }
        }

        var resolved = new List<(MigrationAttribute Attribute, Migration Instance)>();
        foreach (var group in candidates.GroupBy(candidate => candidate.Attribute!.Id, StringComparer.Ordinal))
        {
            var types = group.Select(candidate => candidate.Type).Distinct().ToList();
            if (types.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Two migrations share the id '{group.Key}': {string.Join(", ", types.Select(type => type.FullName))}. " +
                    "Give them distinct titles or timestamps.");
            }

            // Prefer an explicitly registered instance; fall back to activating the scanned type.
            var winner = group.FirstOrDefault(candidate => candidate.Instance is not null);
            resolved.Add((group.First().Attribute!,
                winner.Instance ?? (Migration)Activator.CreateInstance(types[0])!));
        }

        return resolved
            .OrderBy(candidate => candidate.Attribute.Date)
            .ThenBy(candidate => candidate.Attribute.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static MigrationAttribute? Describe(Type type) => type.GetCustomAttribute<MigrationAttribute>();
}
