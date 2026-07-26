using System.Diagnostics;
using System.Text.Json;

namespace eQuantic.Core.Data.Tools;

/// <summary>
///     The project the tool was pointed at, and everything it has to ask MSBuild rather than guess: which
///     framework to load, where the compiled assembly landed, what namespace generated files belong in.
/// </summary>
internal sealed class TargetProject
{
    private TargetProject(string path, string directory, string rootNamespace, string framework, string assembly)
    {
        Path = path;
        Directory = directory;
        RootNamespace = rootNamespace;
        Framework = framework;
        Assembly = assembly;
    }

    /// <summary>The project file.</summary>
    public string Path { get; }

    /// <summary>The directory the project file sits in.</summary>
    public string Directory { get; }

    /// <summary>The project's root namespace, which generated files extend.</summary>
    public string RootNamespace { get; }

    /// <summary>The framework the tool loads the project under.</summary>
    public string Framework { get; }

    /// <summary>The compiled assembly.</summary>
    public string Assembly { get; }

    /// <summary>Finds, builds and inspects the project.</summary>
    /// <param name="hint">A path to a project file or a directory, or <c>null</c> for the current directory.</param>
    /// <param name="configuration">The build configuration.</param>
    /// <param name="build">Whether to build before reading the output path.</param>
    public static TargetProject Open(string? hint, string configuration, bool build)
    {
        var path = Locate(hint);
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;

        var properties = Read(path, ["TargetFramework", "TargetFrameworks", "RootNamespace"]);
        var framework = Choose(properties);

        if (build)
        {
            Console.WriteLine($"Building {System.IO.Path.GetFileName(path)} ({framework})…");
            Run("build", [path, "-c", configuration, "-f", framework, "--nologo", "-v", "quiet"]);
        }

        var output = Read(path, ["TargetPath", "AssemblyName"],
            ["-p:TargetFramework=" + framework, "-p:Configuration=" + configuration]);

        var assembly = output.GetValueOrDefault("TargetPath", string.Empty);
        if (string.IsNullOrWhiteSpace(assembly) || !File.Exists(assembly))
        {
            throw new ToolException(
                $"The project built, but its assembly was not where MSBuild said it would be ('{assembly}'). " +
                "Build it yourself and run again with --no-build.");
        }

        var rootNamespace = properties.GetValueOrDefault("RootNamespace", string.Empty);
        if (string.IsNullOrWhiteSpace(rootNamespace))
        {
            rootNamespace = output.GetValueOrDefault("AssemblyName", "Migrations");
        }

        return new TargetProject(path, directory, rootNamespace, framework, assembly);
    }

    private static string Locate(string? hint)
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
                $"No project in '{directory}'. Run the tool from a project directory, or pass --project."),
            _ => throw new ToolException(
                $"'{directory}' holds {projects.Length} projects. Say which one with --project."),
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
