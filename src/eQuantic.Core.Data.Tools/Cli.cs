namespace eQuantic.Core.Data.Tools;

/// <summary>The command line: a small, hand-read surface, because a tool that generates code should be dull.</summary>
internal static class Cli
{
    /// <summary>Dispatches the arguments.</summary>
    /// <param name="args">The raw command line.</param>
    public static int Run(string[] args)
    {
        // Anything past `--` belongs to the application, not to us.
        var separator = Array.IndexOf(args, "--");
        var passthrough = separator < 0 ? [] : args[(separator + 1)..];
        var own = separator < 0 ? args : args[..separator];

        return own switch
        {
            [] or ["--help"] or ["-h"] or ["help"] => Help(),
            ["--version"] => Version(),
            ["migrations", "add", var name, .. var rest] => AddMigrationCommand.Run(Options(name, rest, passthrough)),
            ["migrations", "add"] => throw new ToolException(
                "A migration needs a name: eqdata migrations add <name>"),
            ["migrations", var unknown, ..] => throw new ToolException(
                $"'{unknown}' is not something migrations do. There is 'add'."),
            ["drift", .. var flags] => DriftCommand.Run(Drift(flags, passthrough)),
            [var unknown, ..] => throw new ToolException($"'{unknown}' is not a command. Run 'eqdata --help'."),
        };
    }

    private static AddMigrationOptions Options(string name, string[] rest, string[] passthrough)
    {
        var common = Parse(rest, "migrations add", output: true);
        return new AddMigrationOptions(name, common.Project, common.Startup, common.Output, common.Configuration,
            common.Build, passthrough);
    }

    private static DriftOptions Drift(string[] flags, string[] passthrough)
    {
        var common = Parse(flags, "drift", output: false);
        return new DriftOptions(common.Project, common.Startup, common.Configuration, common.Build, passthrough);
    }

    /// <summary>The options every command shares. <paramref name="output" /> gates the one that only writes files.</summary>
    private static (string? Project, string? Startup, string Output, string Configuration, bool Build) Parse(
        string[] arguments, string command, bool output)
    {
        string? project = null;
        string? startup = null;
        var folder = "Migrations";
        var configuration = "Debug";
        var build = true;

        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--project" or "-p":
                    project = Value(arguments, ref index);
                    break;
                case "--startup-project" or "-s":
                    startup = Value(arguments, ref index);
                    break;
                case "--output" or "-o" when output:
                    folder = Value(arguments, ref index);
                    break;
                case "--configuration" or "-c":
                    configuration = Value(arguments, ref index);
                    break;
                case "--no-build":
                    build = false;
                    break;
                default:
                    throw new ToolException($"'{arguments[index]}' is not an option of '{command}'.");
            }
        }

        return (project, startup, folder, configuration, build);
    }

    private static string Value(string[] arguments, ref int index)
    {
        var option = arguments[index];
        if (++index >= arguments.Length)
        {
            throw new ToolException($"'{option}' needs a value.");
        }

        return arguments[index];
    }

    private static int Version()
    {
        Console.WriteLine(typeof(Cli).Assembly.GetName().Version?.ToString(3) ?? "unknown");
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine(
            """
            eqdata — migrations for eQuantic.Core.Data

              eqdata migrations add <name> [options]

                Compares the model against the snapshot committed beside it and writes the migration that
                carries one to the other, together with the new snapshot. Both files are yours to read and
                edit before anything runs.

                -o, --output <folder>       Where the files go, relative to the project. Defaults to Migrations.

              eqdata drift [options]

                Opens the database and says how it differs from the model — a column altered by hand, a
                migration that stopped halfway, an environment restored from an older backup. The migration
                history cannot answer this; only looking can. Exits non-zero when the difference is one the
                application would fail on, which makes it usable as a deployment gate.

              Both commands take:

                -p, --project <path>        Where the migrations belong, and whose namespace they take.
                                            Defaults to the current directory.
                -s, --startup-project <p>   The application to run — the one that builds the services. Defaults
                                            to --project, which is right when the model lives in the
                                            application. Name it separately when the model is in a library:
                                            a library produces no runtimeconfig, so it cannot be run.
                -c, --configuration <name>  The build configuration. Defaults to Debug.
                    --no-build              Use the assembly as it is instead of building first.
                    -- <args>               Passed to the application's IDesignTimeServices.

            The tool reads the model from the application itself, through one class that builds its services:

                public sealed class DesignTimeServices : IDesignTimeServices
                {
                    public IServiceProvider Create(string[] args)
                    {
                        var services = new ServiceCollection();
                        services.AddPostgreSqlDatabase(/* the configuration the application uses */);
                        return services.BuildServiceProvider();
                    }
                }
            """);
        return 0;
    }
}
