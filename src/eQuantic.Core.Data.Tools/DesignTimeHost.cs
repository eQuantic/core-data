using System.Reflection;
using System.Runtime.Loader;
using eQuantic.Core.Data.Evolution;

namespace eQuantic.Core.Data.Tools;

/// <summary>
///     Loads the target application and asks it for its services, so everything the tool reports comes from the
///     application's own registrations rather than a second opinion about them.
/// </summary>
internal sealed class DesignTimeHost
{
    private readonly Assembly _assembly;

    private DesignTimeHost(Assembly assembly, IServiceProvider services)
    {
        _assembly = assembly;
        Services = services;
    }

    /// <summary>The application's configured services.</summary>
    public IServiceProvider Services { get; }

    /// <summary>Loads the project's assembly and builds its services.</summary>
    /// <param name="project">The project to enter.</param>
    /// <param name="arguments">Anything passed after <c>--</c>.</param>
    public static DesignTimeHost Enter(TargetProject project, string[] arguments)
    {
        var context = new TargetContext(project.Assembly);
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(project.Assembly));

        var factories = Types(assembly)
            .Where(type => typeof(IDesignTimeServices).IsAssignableFrom(type) &&
                           type is { IsAbstract: false, IsInterface: false })
            .ToList();

        if (factories.Count == 0)
        {
            throw new ToolException(
                $"'{assembly.GetName().Name}' has no {nameof(IDesignTimeServices)}. The tool reads the model from " +
                "the application's own registrations, so it needs one class that builds them:" +
                Environment.NewLine + Environment.NewLine +
                "    public sealed class DesignTimeServices : IDesignTimeServices" + Environment.NewLine +
                "    {" + Environment.NewLine +
                "        public IServiceProvider Create(string[] args)" + Environment.NewLine +
                "        {" + Environment.NewLine +
                "            var services = new ServiceCollection();" + Environment.NewLine +
                "            services.AddPostgreSqlDatabase(/* the configuration the application uses */);" +
                Environment.NewLine +
                "            return services.BuildServiceProvider();" + Environment.NewLine +
                "        }" + Environment.NewLine +
                "    }");
        }

        if (factories.Count > 1)
        {
            throw new ToolException(
                $"'{assembly.GetName().Name}' has {factories.Count} implementations of " +
                $"{nameof(IDesignTimeServices)} ({string.Join(", ", factories.Select(type => type.Name))}). " +
                "The tool cannot choose between them; leave one.");
        }

        var factory = (IDesignTimeServices)Activator.CreateInstance(factories[0])!;
        var services = factory.Create(arguments)
            ?? throw new ToolException($"'{factories[0].Name}' returned no services.");

        return new DesignTimeHost(assembly, services);
    }

    /// <summary>Resolves a service the application must have registered.</summary>
    /// <typeparam name="TService">The service.</typeparam>
    /// <param name="missing">What to say when it is not there.</param>
    public TService Require<TService>(string missing) where TService : notnull =>
        (TService?)Services.GetService(typeof(TService)) ?? throw new ToolException(missing);

    /// <summary>The snapshot committed with the last change, or nothing the first time.</summary>
    public ModelSnapshot? LastSnapshot()
    {
        var files = Types(_assembly)
            .Where(type => typeof(IModelSnapshotFile).IsAssignableFrom(type) &&
                           type is { IsAbstract: false, IsInterface: false })
            .ToList();

        return files.Count switch
        {
            0 => null,
            1 => ((IModelSnapshotFile)Activator.CreateInstance(files[0])!).Model,
            _ => throw new ToolException(
                $"There are {files.Count} model snapshots in '{_assembly.GetName().Name}' " +
                $"({string.Join(", ", files.Select(type => type.Name))}). A project records one history; delete " +
                "the ones that are not it."),
        };
    }

    /// <summary>Types the assembly can actually produce, ignoring the ones whose dependencies are absent.</summary>
    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    /// <summary>
    ///     Loads the application beside the tool rather than inside it.
    ///     <para>
    ///         One rule matters: an assembly the tool already holds is never loaded a second time. The tool hands
    ///         the application <see cref="IDesignTimeServices" /> and reads <see cref="ModelSnapshot" /> back, and
    ///         those are only the same types if there is one copy of them. A second copy would leave the
    ///         application implementing an interface the tool does not recognise, and the failure would read as
    ///         "no design-time services" when the truth is "two of everything".
    ///     </para>
    /// </summary>
    private sealed class TargetContext(string assemblyPath) : AssemblyLoadContext("eqdata-target")
    {
        private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName name)
        {
            try
            {
                // Asking the default context is what settles ownership: an assembly it can resolve is one the
                // tool itself ships, and it has to come from there whether or not anything has touched it yet.
                // Testing "already loaded" instead is the bug that hides — a contract only reached later gets
                // loaded twice, and a type the tool defines stops matching the one the application implements.
                return Default.LoadFromAssemblyName(name);
            }
            catch (Exception failure) when (failure is FileNotFoundException or FileLoadException
                                                or BadImageFormatException)
            {
                var path = _resolver.ResolveAssemblyToPath(name);
                return path is null ? null : LoadFromAssemblyPath(path);
            }
        }

        protected override IntPtr LoadUnmanagedDll(string name)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
