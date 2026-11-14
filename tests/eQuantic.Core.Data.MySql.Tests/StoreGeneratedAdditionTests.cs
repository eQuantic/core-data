using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.MySql.Extensions;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Repository;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace eQuantic.Core.Data.MySql.Tests;

/// <summary>A row whose number and times the store assigns, once those members are added to a table that has rows.</summary>
public sealed class Stamp : IEntity<Guid>
{
    public Guid Id { get; set; }

    public string Note { get; set; } = "";

    public long Number { get; set; }

    public DateTime StampedAt { get; set; }

    public DateTime IssuedAt { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>
///     Why a generated migration asks no value for a store-generated member it adds on MySQL and MariaDB: an
///     <c>AUTO_INCREMENT</c> column added to a table numbers every row in it, a sequence's default (MariaDB) and a
///     clock's are given to every row, and so is a declared default.
/// </summary>
internal static class StoreGeneratedAdditionScenario
{
    public static void Configure(RelationalModelBuilder model) => model.Entity<Stamp>(entity => entity
        .Table("stamps")
        .Generated(x => x.Number)
        .Generated(x => x.StampedAt)
        .Generated(x => x.IssuedAt, "(UTC_TIMESTAMP(6))"));

    public static async Task<object?> ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    /// <summary>Adds the members to a table holding three rows, through the provider registered in <paramref name="services" />.</summary>
    public static async Task RunAsync(string connectionString, IServiceCollection services)
    {
        await ExecuteAsync(connectionString, "CREATE TABLE stamps (id char(36) PRIMARY KEY, note varchar(255))");
        await ExecuteAsync(connectionString, "INSERT INTO stamps (id, note) VALUES (UUID(), 'a'), (UUID(), 'b'), (UUID(), 'c')");

        services.AddMySqlMigrations();
        await using (var provider = services.BuildServiceProvider())
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMigrationExecutor>().ApplyAsync(new MigrationBuilder()
                .For<Stamp>(stamp => stamp.AddField(x => x.Number).AddField(x => x.StampedAt).AddField(x => x.IssuedAt))
                .Operations);
        }

        Assert.That(Convert.ToInt64(await ExecuteAsync(connectionString, "SELECT count(DISTINCT number) FROM stamps")), Is.EqualTo(3L),
            "every row already in the table was numbered");
        Assert.That(Convert.ToInt64(await ExecuteAsync(connectionString,
                "SELECT count(*) FROM stamps WHERE stamped_at IS NOT NULL AND issued_at IS NOT NULL")), Is.EqualTo(3L),
            "the clock's default and the declared one reached every row too");
    }
}

[TestFixture]
public sealed class StoreGeneratedAdditionTests : MySqlIntegrationTest
{
    [Test]
    public async Task Members_added_as_store_generated_are_filled_in_the_rows_already_there()
    {
        var database = "db_" + Guid.NewGuid().ToString("N");
        await StoreGeneratedAdditionScenario.ExecuteAsync(MySqlTestServer.AdminConnectionString, $"CREATE DATABASE {database}");
        var connectionString = new MySqlConnectionStringBuilder(MySqlTestServer.AdminConnectionString) { Database = database }
            .ConnectionString;

        var services = new ServiceCollection();
        services.AddMySqlDatabase(connectionString, StoreGeneratedAdditionScenario.Configure);
        await StoreGeneratedAdditionScenario.RunAsync(connectionString, services);
    }
}
