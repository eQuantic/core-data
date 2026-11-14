using eQuantic.Core.Data.Evolution;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Modeling;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.SqlServer.Extensions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.SqlServer.Tests;

/// <summary>A row whose text and amount the model sizes, so a column added for either has a size to honour.</summary>
public sealed class PricedLine : IEntity<Guid>
{
    public Guid Id { get; set; }

    [Facet(Length = 120)]
    public string Label { get; set; } = "";

    [Facet(Precision = 12, Scale = 3)]
    public decimal Price { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>
///     A column added to a table is sized the way the model declares it, as a created one is: the drift check
///     expects that size, so an added column that missed it would be reported on the next check.
/// </summary>
[TestFixture]
public sealed class AddedFieldFacetsTests : SqlServerIntegrationTest
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
    public async Task An_added_member_takes_the_size_its_facet_declares()
    {
        var database = "db_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(SqlServerTestServer.AdminConnectionString, $"CREATE DATABASE {database}");
        var connectionString = new SqlConnectionStringBuilder(SqlServerTestServer.AdminConnectionString) { InitialCatalog = database }
            .ConnectionString;

        var services = new ServiceCollection();
        services.AddSqlServerDatabase(connectionString, model => model.Entity<PricedLine>(entity => entity.Table("priced_lines")));
        services.AddSqlServerMigrations();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<IMigrationExecutor>();

        // The table as it stood before the two members existed.
        await executor.ApplyAsync(new MigrationBuilder().For<PricedLine>(line => line.EnsureCollection()).Operations);
        await ExecuteAsync(connectionString, "ALTER TABLE priced_lines DROP COLUMN label, price");

        await executor.ApplyAsync(new MigrationBuilder()
            .For<PricedLine>(line => line.AddField(x => x.Label).AddField(x => x.Price)).Operations);

        Assert.That(await ExecuteAsync(connectionString,
                "SELECT CONCAT(DATA_TYPE, '(', CHARACTER_MAXIMUM_LENGTH, ')') FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_NAME = 'priced_lines' AND COLUMN_NAME = 'label'"),
            Is.EqualTo("nvarchar(120)"), "[Facet(Length = 120)] sizes an added column the way it sizes a created one");
        Assert.That(await ExecuteAsync(connectionString,
                "SELECT CONCAT(DATA_TYPE, '(', NUMERIC_PRECISION, ',', NUMERIC_SCALE, ')') FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_NAME = 'priced_lines' AND COLUMN_NAME = 'price'"),
            Is.EqualTo("numeric(12,3)"), "[Facet(Precision = 12, Scale = 3)] too, in the spelling a created column takes");

        var source = scope.ServiceProvider.GetRequiredService<IDatabaseSnapshotSource>();
        Assert.That(DriftComparer.Compare(source.Expect(), await source.ObserveAsync()).Findings, Is.Empty,
            "the drift check expects the sized types, so the added columns have to land on them");
    }
}
