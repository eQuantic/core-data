using eQuantic.Core.Data.Evolution;

namespace eQuantic.Core.Data.Tools;

/// <summary>
///     Generates the migration that carries the model from the last snapshot to where it stands now, and the new
///     snapshot that records where that is.
///     <para>
///         The two files are written together or not at all. A snapshot that moved past a change nobody generated
///         is worse than no snapshot: the next comparison starts from a state the database was never brought to,
///         and the missing change is never mentioned again.
///     </para>
/// </summary>
internal static class AddMigrationCommand
{
    /// <summary>Runs the command.</summary>
    /// <param name="options">What was asked for on the command line.</param>
    public static int Run(AddMigrationOptions options)
    {
        var project = TargetProject.Open(options.Project, options.Configuration, options.Build);
        var host = DesignTimeHost.Enter(project, options.Arguments);

        var source = host.Require<IModelSnapshotSource>(
            "The application's services describe no model. Registering a store inside the design-time services — " +
            "AddPostgreSqlDatabase, AddMongoDatabase, and the rest — registers one along with it.");

        var current = source.Describe();
        var previous = host.LastSnapshot() ?? ModelSnapshot.Empty(source.Provider);
        var difference = ModelDiffer.Compare(previous, current);

        if (difference.Refusals.Count > 0)
        {
            Report(difference);
            return 1;
        }

        if (difference.IsEmpty)
        {
            Console.WriteLine("The model and the last snapshot agree. Nothing to generate.");
            return 0;
        }

        var stamp = DateTime.UtcNow;
        var directory = Path.Combine(project.Directory, options.Output);
        var namespaceName = $"{project.RootNamespace}.{options.Output.Replace(Path.DirectorySeparatorChar, '.')}";

        var migrationPath = Path.Combine(directory, $"{stamp:yyyyMMddHHmmss}_{options.Name}.cs");
        if (File.Exists(migrationPath))
        {
            throw new ToolException($"'{migrationPath}' already exists.");
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(migrationPath, MigrationWriter.Write(difference, options.Name, namespaceName, stamp));
        File.WriteAllText(Path.Combine(directory, "DataModelSnapshot.g.cs"),
            ModelSnapshotWriter.Write(current, namespaceName));

        Console.WriteLine($"  {Relative(project.Directory, migrationPath)}");
        Console.WriteLine($"  {Relative(project.Directory, Path.Combine(directory, "DataModelSnapshot.g.cs"))}");
        Console.WriteLine();

        Summarize(difference);

        var decisions = difference.Changes.Count(change => change.NeedsValue) +
                        difference.Changes.Count(change => change.AmbiguousRenameHint is not null);

        if (decisions > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{decisions} of these need an answer only you have, so the migration is written to " +
                              "fail the build until it is given. Each one says what it needs.");
        }

        Console.WriteLine();
        Console.WriteLine($"Migrations are registered by hand — add .Add<{Identifier(options.Name)}>() where the " +
                          "others are.");
        return 0;
    }

    private static void Summarize(ModelDifference difference)
    {
        foreach (var group in difference.Changes.GroupBy(change => change.EntityType).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine(group.Key);
            foreach (var change in group)
            {
                Console.WriteLine($"  {Describe(change)}");
            }
        }
    }

    private static string Describe(ModelChange change) => change.Kind switch
    {
        ModelChangeKind.AddCollection => $"create {change.To}",
        ModelChangeKind.DropCollection => $"drop {change.From}",
        ModelChangeKind.RenameCollection => $"move {change.From} to {change.To}",
        ModelChangeKind.AddField when change.NeedsValue => $"add {change.To} — needs a value",
        ModelChangeKind.AddField => $"add {change.To}",
        ModelChangeKind.DropField when change.AmbiguousRenameHint is not null => $"drop {change.From} — or is it a rename?",
        ModelChangeKind.DropField => $"drop {change.From}",
        ModelChangeKind.RenameField => $"rename {change.From} to {change.To}",
        ModelChangeKind.ConvertField => $"convert {change.Member} from {change.From} to {change.To}",
        ModelChangeKind.ChangeFacets => $"resize {change.Member} from {change.From} to {change.To}",
        _ => change.Kind.ToString(),
    };

    private static void Report(ModelDifference difference)
    {
        Console.Error.WriteLine("Nothing was written. The model asks for changes this store cannot make:");
        Console.Error.WriteLine();

        foreach (var refusal in difference.Refusals)
        {
            Console.Error.WriteLine($"  {refusal.EntityType}");
            Console.Error.WriteLine($"    {refusal.Reason}");
            Console.Error.WriteLine($"    Instead: {refusal.Alternative}");
            Console.Error.WriteLine();
        }

        Console.Error.WriteLine(
            "Generating the rest would move the snapshot past these without them ever being applied. Resolve them, " +
            "then run again.");
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string Identifier(string name) =>
        new(name.Where(character => char.IsLetterOrDigit(character) || character == '_').ToArray());
}

/// <summary>What <c>migrations add</c> was asked for.</summary>
/// <param name="Name">The migration's name.</param>
/// <param name="Project">The project to read, or <c>null</c> for the current directory.</param>
/// <param name="Output">The folder generated files go in, relative to the project.</param>
/// <param name="Configuration">The build configuration.</param>
/// <param name="Build">Whether to build the project first.</param>
/// <param name="Arguments">Anything passed after <c>--</c>, handed to the design-time services.</param>
internal sealed record AddMigrationOptions(
    string Name,
    string? Project,
    string Output,
    string Configuration,
    bool Build,
    string[] Arguments);
