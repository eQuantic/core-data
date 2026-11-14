using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.SqlServer.Extensions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.SqlServer.Tests;

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
///     Why a generated migration asks no value for a store-generated member it adds on SQL Server: a <c>NOT NULL</c>
///     column added with a default gives every row already in the table that default, a sequence's next value included.
/// </summary>
[TestFixture]
public sealed class StoreGeneratedAdditionTests : SqlServerIntegrationTest
{
    private static async Task<object?> ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    [Test]
    public async Task Members_added_as_store_generated_are_filled_in_the_rows_already_there()
    {
        var database = "db_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(SqlServerTestServer.AdminConnectionString, $"CREATE DATABASE {database}");
        var connectionString = new SqlConnectionStringBuilder(SqlServerTestServer.AdminConnectionString) { InitialCatalog = database }
            .ConnectionString;
        await ExecuteAsync(connectionString, "CREATE TABLE stamps (id uniqueidentifier PRIMARY KEY, note nvarchar(450))");
        await ExecuteAsync(connectionString,
            "INSERT INTO stamps (id, note) VALUES (NEWID(), N'a'), (NEWID(), N'b'), (NEWID(), N'c')");

        var services = new ServiceCollection();
        services.AddSqlServerDatabase(connectionString, model => model.Entity<Stamp>(entity => entity
            .Table("stamps")
            .Generated(x => x.Number)
            .Generated(x => x.StampedAt)
            .Generated(x => x.IssuedAt, "SYSUTCDATETIME()")));
        services.AddSqlServerMigrations();
        await using (var provider = services.BuildServiceProvider())
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMigrationExecutor>().ApplyAsync(new MigrationBuilder()
                .For<Stamp>(stamp => stamp.AddField(x => x.Number).AddField(x => x.StampedAt).AddField(x => x.IssuedAt))
                .Operations);
        }

        Assert.That(await ExecuteAsync(connectionString, "SELECT count(DISTINCT number) FROM stamps"), Is.EqualTo(3),
            "the sequence's default numbered every row already in the table");
        Assert.That(await ExecuteAsync(connectionString,
                "SELECT count(*) FROM stamps WHERE stamped_at IS NOT NULL AND issued_at IS NOT NULL"), Is.EqualTo(3),
            "the clock's default and the declared one reached every row too");
    }
}
