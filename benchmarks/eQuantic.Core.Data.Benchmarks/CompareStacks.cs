using eQuantic.Core.Data.PostgreSql.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace eQuantic.Core.Data.Benchmarks;

/// <summary>
///     The four compared stacks over the same table, each set up the way its own documentation recommends:
///     eQuantic through DI (scope per operation, like a request), EF Core through a pooled context factory with
///     no-tracking reads, Dapper and raw Npgsql over a shared <see cref="NpgsqlDataSource" />.
/// </summary>
public sealed class CompareStacks : IDisposable
{
    public ServiceProvider Equantic { get; }
    public PooledDbContextFactory<BenchDbContext> EfFactory { get; }
    public NpgsqlDataSource DataSource { get; }

    public CompareStacks()
    {
        var connectionString = BenchmarkEnvironment.ConnectionString;

        var services = new ServiceCollection();
        services.AddPostgreSqlDatabase(connectionString, model => model
            .Entity<BenchProduct>(entity => entity.Table(BenchmarkEnvironment.Table)));
        services.AddPostgreSqlRepositories();
        Equantic = services.BuildServiceProvider();

        EfFactory = new PooledDbContextFactory<BenchDbContext>(
            new DbContextOptionsBuilder<BenchDbContext>().UseNpgsql(connectionString).Options);

        DataSource = NpgsqlDataSource.Create(connectionString);

        // Dapper maps snake_case columns onto PascalCase members the same way the other stacks do.
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public void Dispose()
    {
        Equantic.Dispose();
        DataSource.Dispose();
    }
}
