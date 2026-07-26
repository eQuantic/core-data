using eQuantic.Core.Data.Evolution;

namespace eQuantic.Core.Data.Tools;

/// <summary>
///     Looks at the database and says how it differs from the model.
///     <para>
///         There are three versions of the truth in a project like this, and the migration history only reconciles
///         two of them. It records which changes ran; it cannot tell you that someone widened a column by hand on
///         staging, that a migration failed halfway and left the store between states, or that an environment was
///         restored from a backup taken before the last release. The only way to know is to look, and this looks.
///     </para>
/// </summary>
internal static class DriftCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="options">What was asked for on the command line.</param>
    public static int Run(DriftOptions options)
    {
        var project = TargetProject.Open(options.Project, options.Configuration, options.Build);
        var host = DesignTimeHost.Enter(project, options.Arguments);

        var pending = Pending(host);
        var report = Observe(host);

        if (report.IsClean && pending == 0)
        {
            Console.WriteLine($"The {report.Provider} database matches the model.");
            return 0;
        }

        if (!report.IsClean)
        {
            Describe(report);
        }

        if (pending > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Separately, the model has moved {pending} step(s) beyond the snapshot committed " +
                              "beside it. Those are changes nobody has generated yet — run 'migrations add'. They " +
                              "are not drift; the database is behind the code on purpose until a migration runs.");
        }

        // A database missing something the model reads will fail at runtime; a column nobody mapped will not.
        return report.Breaks ? 1 : 0;
    }

    private static DriftReport Observe(DesignTimeHost host)
    {
        var source = host.Require<IDatabaseSnapshotSource>(
            "This store cannot describe itself, so there is nothing to compare the model against — which is not " +
            "the same as there being no drift. PostgreSQL, MySQL, MariaDB, SQL Server and Cassandra keep a schema " +
            "to read, and Cosmos DB keeps its containers and partition keys. MongoDB keeps neither: a collection " +
            "has no shape beyond the documents in it, and sampling those would describe the ones that came back " +
            "rather than the collection.");

        var observed = source.ObserveAsync().GetAwaiter().GetResult();
        return DriftComparer.Compare(source.Expect(), observed);
    }

    /// <summary>How far the model has moved past the snapshot — a different question from drift, asked here too.</summary>
    private static int Pending(DesignTimeHost host)
    {
        if (host.LastSnapshot() is not { } snapshot)
        {
            return 0;
        }

        var model = host.Require<IModelSnapshotSource>(
            "The application's services describe no model.");

        var difference = ModelDiffer.Compare(snapshot, model.Describe());
        return difference.Changes.Count + difference.Refusals.Count;
    }

    private static void Describe(DriftReport report)
    {
        Console.WriteLine($"The {report.Provider} database and the model disagree:");
        Console.WriteLine();

        foreach (var group in report.Findings
                     .GroupBy(finding => (finding.EntityType, finding.Collection))
                     .OrderBy(group => group.Key.Collection, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {group.Key.Collection}  ({group.Key.EntityType})");
            foreach (var finding in group)
            {
                Console.WriteLine($"    {Say(finding)}");
            }

            Console.WriteLine();
        }

        if (report.NeedsRebuild)
        {
            Console.WriteLine(
                "A partition key is fixed when a table or container is created, so the one above cannot be " +
                "migrated at all. Map the new shape alongside, copy the data across, and retire the old one once " +
                "the readers have moved.");
            Console.WriteLine();
        }

        Console.WriteLine(report.Breaks
            ? "The application reads these on every query and the store does not hold them as it expects. Close " +
              "the difference — by migrating the database, or by correcting the model if the database is right."
            : "Nothing here stops the application working — a column it does not map is somebody else's.");
    }

    private static string Say(DriftFinding finding) => finding.Kind switch
    {
        DriftKind.MissingCollection => "the table is not there",
        DriftKind.MissingField => $"{finding.Field} is not there — the model expects {finding.Expected}",
        DriftKind.UnexpectedField => $"{finding.Field} is there ({finding.Found}) and the model does not map it",
        DriftKind.TypeDiffers => $"{finding.Field} is {finding.Found}, and the model expects {finding.Expected}",
        DriftKind.NullabilityDiffers => $"{finding.Field} is {finding.Found}, and the model expects {finding.Expected}",
        DriftKind.PartitionKeyDiffers =>
            $"the data is distributed by {finding.Found}, and the model says {finding.Expected}",
        _ => finding.Kind.ToString(),
    };
}

/// <summary>What <c>drift</c> was asked for.</summary>
/// <param name="Project">The project to read, or <c>null</c> for the current directory.</param>
/// <param name="Configuration">The build configuration.</param>
/// <param name="Build">Whether to build the project first.</param>
/// <param name="Arguments">Anything passed after <c>--</c>, handed to the design-time services.</param>
internal sealed record DriftOptions(string? Project, string Configuration, bool Build, string[] Arguments);
