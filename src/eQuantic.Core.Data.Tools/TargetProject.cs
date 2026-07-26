using System.Diagnostics;
using System.Text.Json;

namespace eQuantic.Core.Data.Tools;

/// <summary>
///     The project the tool was pointed at, and everything it has to ask MSBuild rather than guess: which
///     framework to load, where the compiled assembly landed, what namespace generated files belong in.
/// </summary>
internal sealed class TargetProject
{
    private TargetProject(string path, string directory, string rootNamespace, string framework, string assembly,
        string? ownAssembly)
    {
        Path = path;
        Directory = directory;
        RootNamespace = rootNamespace;
        Framework = framework;
        Assembly = assembly;
        OwnAssembly = ownAssembly;
    }

    /// <summary>The project file.</summary>
    public string Path { get; }

    /// <summary>The directory the project file sits in.</summary>
    public string Directory { get; }

    /// <summary>The project's root namespace, which generated files extend.</summary>
    public string RootNamespace { get; }

    /// <summary>The framework the tool loads the project under.</summary>
    public string Framework { get; }

    /// <summary>The compiled assembly the tool loads and runs — the startup project's.</summary>
    public string Assembly { get; }

    /// <summary>
    ///     The project's own compiled assembly, when it is not the one being run. That is where a snapshot and a
    ///     design-time factory may live if the model sits in a library and an application starts it — so both are
    ///     searched, rather than assuming the two roles belong to one project.
    /// </summary>
    public string? OwnAssembly { get; }

    /// <summary>Finds, builds and inspects the projects.</summary>
    /// <param name="hint">
    ///     The project generated files belong to, and whose namespace they take. A path to a project file or a
    ///     directory, or <c>null</c> for the current directory.
    /// </param>
    /// <param name="startupHint">
    ///     The project to run — the application that builds the services. Defaults to <paramref name="hint" />,
    ///     which is right when the model lives in the application itself.
    /// </param>
    /// <param name="configuration">The build configuration.</param>
    /// <param name="build">Whether to build before reading the output paths.</param>
    public static TargetProject Open(string? hint, string? startupHint, string configuration, bool build)
    {
        var path = Locate(hint, "--project");
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        var properties = Read(path, ["TargetFramework", "TargetFrameworks", "RootNamespace"]);

        // The project to run is the project itself unless another one was named.
        var startup = startupHint is null ? path : Locate(startupHint, "--startup-project");
        var sameProject = string.Equals(System.IO.Path.GetFullPath(startup), System.IO.Path.GetFullPath(path),
            StringComparison.OrdinalIgnoreCase);

        var framework = Choose(sameProject
            ? properties
            : Read(startup, ["TargetFramework", "TargetFrameworks"]));

        if (build)
        {
            Console.WriteLine($"Building {System.IO.Path.GetFileName(startup)} ({framework})…");
            Run("build", [startup, "-c", configuration, "-f", framework, "--nologo", "-v", "quiet"]);
        }

        var output = Read(startup, ["TargetPath", "AssemblyName"],
            ["-p:TargetFramework=" + framework, "-p:Configuration=" + configuration]);

        var assembly = output.GetValueOrDefault("TargetPath", string.Empty);
        if (string.IsNullOrWhiteSpace(assembly) || !File.Exists(assembly))
        {
            throw new ToolException(
                $"'{System.IO.Path.GetFileName(startup)}' built, but its assembly was not where MSBuild said it " +
                $"would be ('{assembly}'). Build it yourself and run again with --no-build.");
        }

        if (!File.Exists(System.IO.Path.ChangeExtension(assembly, ".runtimeconfig.json")))
        {
            throw new ToolException(
                $"'{System.IO.Path.GetFileName(startup)}' is a library: it produces no runtimeconfig.json, and " +
                "without one the assemblies your model depends on cannot be located. Point --startup-project at " +
                "the application that starts this model, and keep --project where the migrations belong.");
        }

        // The project's own assembly, when it is a different one: a snapshot committed beside the model lives
        // there, not in the application that happens to run it.
        string? own = null;
        if (!sameProject)
        {
            var mine = Read(path, ["TargetPath", "AssemblyName"],
                ["-p:TargetFramework=" + Choose(properties), "-p:Configuration=" + configuration])
                .GetValueOrDefault("TargetPath", string.Empty);
            own = File.Exists(mine) ? mine : null;
        }

        var rootNamespace = properties.GetValueOrDefault("RootNamespace", string.Empty);
        if (string.IsNullOrWhiteSpace(rootNamespace))
        {
            rootNamespace = Read(path, ["AssemblyName", "MSBuildProjectName"])
                .GetValueOrDefault("AssemblyName", "Migrations");
        }

        return new TargetProject(path, directory, rootNamespace, framework, assembly, own);
    }

    private static string Locate(string? hint, string option)
    {
        if (!string.IsNullOrWhiteSpace(hint) && File.Exists(hint))
        {
            return hint;
        }

        var directory = string.IsNullOrWhiteSpace(hint) ? System.IO.Directory.GetCurrentDirectory() : hint;
        if (!System.IO.Directory.Exists(directory))
        {
            throw new ToolException($"There is no project or directory at '{hint}'.");
        }

        var projects = System.IO.Directory.GetFiles(directory, "*.csproj");
        return projects.Length switch
        {
            1 => projects[0],
            0 => throw new ToolException(
                $"No project in '{directory}'. Run the tool from a project directory, or pass {option}."),
            _ => throw new ToolException(
                $"'{directory}' holds {projects.Length} projects. Say which one with {option}."),
        };
    }

    /// <summary>Picks the framework to load under, preferring the newest when the project multi-targets.</summary>
    private static string Choose(IReadOnlyDictionary<string, string> properties)
    {
        var single = properties.GetValueOrDefault("TargetFramework", string.Empty);
        if (!string.IsNullOrWhiteSpace(single))
        {
            return single;
        }

        var all = properties.GetValueOrDefault("TargetFrameworks", string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (all.Length == 0)
        {
            throw new ToolException("The project declares no target framework.");
        }

        return all.OrderByDescending(framework => framework, StringComparer.Ordinal).First();
    }

    private static Dictionary<string, string> Read(string project, string[] names, string[]? extra = null)
    {
        // Two or more properties make MSBuild answer in JSON, which is the only shape worth parsing.
        string[] arguments = [project, "--nologo", .. names.Select(name => "-getProperty:" + name), .. extra ?? []];

        var output = Run("msbuild", arguments);
        try
        {
            var properties = JsonDocument.Parse(output).RootElement.GetProperty("Properties");
            return properties.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty);
        }
        catch (JsonException)
        {
            throw new ToolException(
                $"MSBuild did not answer with the project's properties. It said:{Environment.NewLine}{output}");
        }
    }

    private static string Run(string verb, string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        start.ArgumentList.Add(verb);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new ToolException("The .NET SDK could not be started. Is 'dotnet' on the path?");

        // Read before waiting: a full pipe would otherwise block the child forever.
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new ToolException(
                $"'dotnet {verb}' failed.{Environment.NewLine}{(output + error).Trim()}");
        }

        return output;
    }
}
