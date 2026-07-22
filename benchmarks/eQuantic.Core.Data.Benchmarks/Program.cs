using BenchmarkDotNet.Running;
using eQuantic.Core.Data.Benchmarks;
using Testcontainers.PostgreSql;

// One PostgreSQL container for the whole run, started here in the parent process: the comparative
// benchmarks (Compare*) reach it through the exported connection string, which BenchmarkDotNet's
// child processes inherit. The translation benchmarks need no I/O and simply ignore it.
var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
await container.StartAsync();
try
{
    var connectionString = container.GetConnectionString();
    await BenchmarkEnvironment.SeedAsync(connectionString);
    Environment.SetEnvironmentVariable(BenchmarkEnvironment.ConnectionVariable, connectionString);

    BenchmarkSwitcher.FromAssembly(typeof(BenchmarkEnvironment).Assembly).Run(args);
}
finally
{
    await container.DisposeAsync();
}
