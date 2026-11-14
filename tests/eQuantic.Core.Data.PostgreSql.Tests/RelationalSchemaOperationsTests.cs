using System.Data.Common;
using System.Text.Json;
using eQuantic.Core.Data.Evolution;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Relational.Migration;
using eQuantic.Core.Data.Repository;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>
///     The three schema changes the generator used to hand back rather than perform: resizing a column, renaming a
///     table, and dropping one. Each is checked by asking the database afterwards, not by inspecting the SQL — the
///     statement being plausible is not the same as the store having done it.
/// </summary>
[TestFixture]
public sealed class RelationalSchemaOperationsTests : PostgreSqlIntegrationTest
{
    private static async Task ApplyAsync(PostgreSqlTestDatabase db, Action<IMigrationBuilder> configure)
    {
        var builder = new MigrationBuilder();
        configure(builder);
        await new RelationalMigrationExecutor(db.Resolve<DbDataSource>(), db.Resolve<SqlDialect>(),
            db.Resolve<RelationalModel>()).ApplyAsync(builder.Operations);
    }

    private static async Task<string?> ScalarAsync(PostgreSqlTestDatabase db, string sql)
    {
        await using var connection = await db.Resolve<DbDataSource>().OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    private static Task<string?> TypeOfAsync(PostgreSqlTestDatabase db, string table, string column) =>
        ScalarAsync(db, $"""
            SELECT format_type(a.atttypid, a.atttypmod)
            FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid
            WHERE c.relname = '{table}' AND a.attname = '{column}'
            """);

    private static Task<string?> ExistsAsync(PostgreSqlTestDatabase db, string table) =>
        ScalarAsync(db, $"SELECT count(*) FROM pg_class WHERE relname = '{table}' AND relkind = 'r'");

    [Test]
    public async Task Resizing_restates_a_column_to_the_size_the_model_declares()
    {
        using var db = await NewSchemaAsync();
        Assert.That(await TypeOfAsync(db, "order_lines", "product"), Is.EqualTo("character varying(200)"),
            "the model declares 200, and the engine created it that way");

        // Narrowed by hand, as a hand-run script or an older deployment would leave it.
        await using (var connection = await db.Resolve<DbDataSource>().OpenConnectionAsync())
        {
            await using var narrow = connection.CreateCommand();
            narrow.CommandText = "ALTER TABLE order_lines ALTER COLUMN product TYPE varchar(10)";
            await narrow.ExecuteNonQueryAsync();
        }

        await ApplyAsync(db, migration => migration.For<OrderLine>(line => line.ResizeField(x => x.Product)));

        Assert.That(await TypeOfAsync(db, "order_lines", "product"), Is.EqualTo("character varying(200)"),
            "the size comes from the model, never from the caller, so the column lands where the mapping says");
    }

    [Test]
    public async Task Resizing_a_column_already_the_right_size_changes_nothing()
    {
        using var db = await NewSchemaAsync();

        await ApplyAsync(db, migration => migration.For<OrderLine>(line => line.ResizeField(x => x.Product)));

        Assert.That(await TypeOfAsync(db, "order_lines", "product"), Is.EqualTo("character varying(200)"),
            "restating a column to what it already is has to be safe — a migration gets re-run");
    }

    [Test]
    public async Task Adding_a_member_creates_the_column_at_the_size_the_model_declares()
    {
        using var db = await NewSchemaAsync();
        // The table as it stood before the two members existed.
        await using (var connection = await db.Resolve<DbDataSource>().OpenConnectionAsync())
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = "ALTER TABLE order_lines DROP COLUMN product, DROP COLUMN amount";
            await drop.ExecuteNonQueryAsync();
        }

        await ApplyAsync(db, migration => migration.For<OrderLine>(line => line.AddField(x => x.Product).AddField(x => x.Amount)));

        Assert.That(await TypeOfAsync(db, "order_lines", "product"), Is.EqualTo("character varying(200)"),
            "[Facet(Length = 200)] sizes an added column the way it sizes a created one");
        Assert.That(await TypeOfAsync(db, "order_lines", "amount"), Is.EqualTo("numeric(18,2)"),
            "[Facet(Precision = 18, Scale = 2)] too");
        var source = db.Resolve<IDatabaseSnapshotSource>();
        Assert.That(DriftComparer.Compare(source.Expect(), await source.ObserveAsync()).Findings, Is.Empty,
            "the drift check expects the sized type, so an added column has to land on it");
    }

    [Test]
    public async Task An_update_step_writes_a_json_member_and_a_dictionary_document()
    {
        using var db = await NewSchemaAsync();
        var runs = db.Resolve<IAsyncRepository<RunRecord, Guid>>();
        var orders = db.Resolve<IAsyncRepository<SaleOrder, Guid>>();
        var run = new RunRecord();
        var order = new SaleOrder { Id = Guid.NewGuid(), Customer = "alice", CreatedAt = DateTime.UtcNow };
        await runs.AddAsync(run);
        await orders.AddAsync(order);
        await db.Resolve<PostgreSql.Repository.PostgreSqlDefaultUnitOfWork>().CommitAsync();

        using var payload = JsonDocument.Parse("""{"name":"backfill","retries":3}""");
        var attributes = new Dictionary<string, string> { ["tier"] = "gold", ["region"] = "emea" };
        await ApplyAsync(db, migration => migration
            .For<RunRecord>(r => r.Update(x => x.Id == run.Id, set => set.Set(x => x.Payload, payload)))
            .For<SaleOrder>(o => o.Update(x => x.Id == order.Id, set => set.Set(x => x.Attributes, attributes))));

        Assert.That(await ScalarAsync(db, $"SELECT payload ->> 'name' FROM run_record WHERE id = '{run.Id}'"),
            Is.EqualTo("backfill"), "the JSON member reached its jsonb column as a document the store reads inside");
        Assert.That(await ScalarAsync(db, $"SELECT attributes ->> 'region' FROM sale_orders WHERE id = '{order.Id}'"),
            Is.EqualTo("emea"), "the dictionary reached its jsonb column as a document, not as another type");

        using var scope = db.NewScope();
        var loadedRun = await scope.ServiceProvider.GetRequiredService<IAsyncRepository<RunRecord, Guid>>().GetAsync(run.Id);
        var loadedOrder = await scope.ServiceProvider.GetRequiredService<IAsyncRepository<SaleOrder, Guid>>().GetAsync(order.Id);
        Assert.That(loadedRun!.Payload!.RootElement.GetProperty("retries").GetInt32(), Is.EqualTo(3));
        Assert.That(loadedOrder!.Attributes, Is.EqualTo(attributes), "the row holds the dictionary the step set");
    }

    [Test]
    public async Task Renaming_a_table_moves_it()
    {
        using var db = await NewSchemaAsync();

        await ApplyAsync(db, migration => migration.For<SaleOrder>(order =>
            order.RenameCollection("sale_orders", "sale_orders_v2")));

        Assert.That(await ExistsAsync(db, "sale_orders_v2"), Is.EqualTo("1"));
        Assert.That(await ExistsAsync(db, "sale_orders"), Is.EqualTo("0"), "a rename moves it, it does not copy it");
    }

    [Test]
    public async Task Dropping_a_table_removes_it()
    {
        using var db = await NewSchemaAsync();
        Assert.That(await ExistsAsync(db, "legacy_notes"), Is.EqualTo("1"));

        await ApplyAsync(db, migration => migration.For<LegacyNote>(note => note.DropCollection("legacy_notes")));

        Assert.That(await ExistsAsync(db, "legacy_notes"), Is.EqualTo("0"));
    }

    [Test]
    public void The_generator_now_writes_a_resize_and_a_rename_instead_of_handing_them_back()
    {
        var before = new ModelSnapshot("postgresql", [
            new EntitySnapshot("Shop.OrderData", "orders",
                [new FieldSnapshot("Reference", "reference", "System.String") { Length = 50 }]) { Keys = ["Id"] },
        ]);
        var after = new ModelSnapshot("postgresql", [
            new EntitySnapshot("Shop.OrderData", "sale_orders",
                [new FieldSnapshot("Reference", "reference", "System.String") { Length = 200 }]) { Keys = ["Id"] },
        ]);

        var source = MigrationWriter.Write(ModelDiffer.Compare(before, after), "Widen", "Shop.Migrations",
            new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc));

        Assert.That(source, Does.Contain(".ResizeField(x => x.Reference)"));
        Assert.That(source, Does.Contain(".RenameCollection(\"orders\", \"sale_orders\")"));
        Assert.That(source, Does.Not.Contain("#error"),
            "neither of these needs a decision any more — the engine performs them");
    }

    [Test]
    public void A_dropped_entity_is_still_handed_back_rather_than_generated()
    {
        var before = new ModelSnapshot("postgresql", [
            new EntitySnapshot("Shop.OrderData", "orders", []) { Keys = ["Id"] },
        ]);

        var source = MigrationWriter.Write(ModelDiffer.Compare(before, ModelSnapshot.Empty("postgresql")),
            "Retire", "Shop.Migrations", new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc));

        Assert.That(source, Does.Contain("#error"),
            "deleting everything in a table is not a thing to generate from a model diff");
        Assert.That(source, Does.Contain("DropCollection"), "but the operation is named, so it is one edit away");
    }
}
