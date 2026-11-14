using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.PostgreSql.Extensions;
using eQuantic.Core.Data.Repository;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace eQuantic.Core.Data.PostgreSql.Tests;

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
///     Why a generated migration asks no value for a store-generated member it adds on PostgreSQL: the store fills the
///     rows already in the table itself. An identity added to a table numbers every row in it, and a column added with
///     a default gives every row that default.
/// </summary>
[TestFixture]
public sealed class StoreGeneratedAdditionTests : PostgreSqlIntegrationTest
{
    private static async Task<object?> ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    [Test]
    public async Task Members_added_as_store_generated_are_filled_in_the_rows_already_there()
    {
        var database = "db_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(PostgreSqlTestServer.AdminConnectionString, $"CREATE DATABASE {database}");
        var connectionString = new NpgsqlConnectionStringBuilder(PostgreSqlTestServer.AdminConnectionString) { Database = database }
            .ConnectionString;
        await ExecuteAsync(connectionString, "CREATE TABLE stamps (id uuid PRIMARY KEY, note text)");
        await ExecuteAsync(connectionString,
            "INSERT INTO stamps (id, note) VALUES (gen_random_uuid(), 'a'), (gen_random_uuid(), 'b'), (gen_random_uuid(), 'c')");

        var services = new ServiceCollection();
        services.AddPostgreSqlDatabase(connectionString, model => model.Entity<Stamp>(entity => entity
            .Table("stamps")
            .Generated(x => x.Number)
            .Generated(x => x.StampedAt)
            .Generated(x => x.IssuedAt, "now()")));
        services.AddPostgreSqlMigrations();
        await using (var provider = services.BuildServiceProvider())
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMigrationExecutor>().ApplyAsync(new MigrationBuilder()
                .For<Stamp>(stamp => stamp.AddField(x => x.Number).AddField(x => x.StampedAt).AddField(x => x.IssuedAt))
                .Operations);
        }

        Assert.That(await ExecuteAsync(connectionString, "SELECT count(DISTINCT number) FROM stamps"), Is.EqualTo(3L),
            "the identity numbered every row already in the table");
        Assert.That(await ExecuteAsync(connectionString,
                "SELECT count(*) FROM stamps WHERE stamped_at IS NOT NULL AND issued_at IS NOT NULL"), Is.EqualTo(3L),
            "the clock's default and the declared one reached every row too");
    }
}
