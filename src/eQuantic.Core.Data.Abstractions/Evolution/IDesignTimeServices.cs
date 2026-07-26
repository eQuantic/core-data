using System;

namespace eQuantic.Core.Data.Evolution;

/// <summary>
///     The seam the tooling enters an application through. Implement it once, anywhere in the project the tool is
///     pointed at, and <c>eqdata</c> will find it.
///     <para>
///         It hands back the configured <see cref="IServiceProvider" /> rather than the model alone, because
///         reading the model is only half of what the tooling does: comparing it against a live database needs the
///         same connection the application uses, configured the same way. Anything the tool learns, it learns from
///         the application's own registrations.
///     </para>
///     <example>
///         <code>
///         public sealed class DesignTimeServices : IDesignTimeServices
///         {
///             public IServiceProvider Create(string[] args)
///             {
///                 var services = new ServiceCollection();
///                 services.AddPostgreSqlDatabase(
///                     Environment.GetEnvironmentVariable("DB") ?? "Host=localhost;Database=shop;…",
///                     model => model.Entity&lt;OrderData&gt;(order => order.Table("orders").Key(x => x.Id)));
///                 return services.BuildServiceProvider();
///             }
///         }
///         </code>
///     </example>
/// </summary>
public interface IDesignTimeServices
{
    /// <summary>Builds the application's services as the tooling should see them.</summary>
    /// <param name="args">Anything passed after <c>--</c> on the command line.</param>
    IServiceProvider Create(string[] args);
}
