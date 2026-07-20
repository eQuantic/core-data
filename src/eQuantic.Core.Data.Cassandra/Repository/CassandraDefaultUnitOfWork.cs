using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra.Repository;

/// <summary>The ready-to-use Cassandra unit of work; register a custom subclass of <see cref="CassandraUnitOfWork" /> to override behaviour.</summary>
/// <param name="serviceProvider">The service provider (used to resolve repositories).</param>
/// <param name="session">The session.</param>
/// <param name="model">The Cassandra model (tables and keys per entity).</param>
public sealed class CassandraDefaultUnitOfWork(IServiceProvider serviceProvider, ISession session, CassandraModel model)
    : CassandraUnitOfWork(serviceProvider, session, model);
