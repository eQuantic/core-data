using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;

namespace eQuantic.Core.Data.SqlServer.Tests;

/// <summary>
///     The read shapes against a real SQL Server: a shaped include ranked with <c>ROW_NUMBER()</c>, relation counts as
///     correlated subqueries, nulls placed by ordering on whether the column is null, a page without its total, and
///     <c>SELECT DISTINCT</c>.
/// </summary>
[TestFixture]
public sealed class SqlServerReadShapesTests : SqlServerIntegrationTest
{
    private static async Task<(SqlServerTestDatabase Db, ShapeStore Store)> NewStoreAsync()
    {
        var commands = new CommandCapture();
        var db = await NewSchemaAsync(commands.Register);
        return (db, new ShapeStore(type => Resolve(db, type), () => Uow(db).CommitAsync(), commands));
    }

    private static object Resolve(SqlServerTestDatabase db, Type type) =>
        typeof(SqlServerTestDatabase).GetMethod(nameof(SqlServerTestDatabase.Resolve))!.MakeGenericMethod(type).Invoke(db, null)!;

    [Test]
    public async Task An_include_is_filtered_ordered_and_limited_per_parent()
    {
        var (db, store) = await NewStoreAsync();
        using (db)
        {
            await ReadShapeScenarios.TheLastThreeNewestFirst(store);
        }
    }

    [Test]
    public async Task A_relation_is_counted_not_loaded()
    {
        var (db, store) = await NewStoreAsync();
        using (db)
        {
            await ReadShapeScenarios.CountedNotLoaded(store);
        }
    }

    [Test]
    public async Task A_count_s_filter_crosses_the_counted_entity_s_relations()
    {
        var (db, store) = await NewStoreAsync();
        using (db)
        {
            await ReadShapeScenarios.CountFilterCrossesRelations(store);
        }
    }

    [Test]
    public async Task Nulls_are_ordered_first_or_last()
    {
        var (db, store) = await NewStoreAsync();
        using (db)
        {
            await ReadShapeScenarios.NullsFirstOrLast(store);
        }
    }

    [Test]
    public async Task A_page_is_read_without_its_total()
    {
        var (db, store) = await NewStoreAsync();
        using (db)
        {
            await ReadShapeScenarios.TakeTenWithoutTotal(store);
        }
    }

    [Test]
    public async Task A_skip_without_a_take_returns_every_row_after_it()
    {
        var (db, store) = await NewStoreAsync();
        using (db)
        {
            await ReadShapeScenarios.SkipWithoutTakeAndTakeWithoutSkip(store);
        }
    }

    [Test]
    public async Task A_take_of_zero_returns_nothing_without_asking_the_store()
    {
        var (db, store) = await NewStoreAsync();
        using (db)
        {
            await ReadShapeScenarios.TakeZero(store);
        }
    }

    [Test]
    public async Task Includes_of_more_parents_than_one_statement_binds()
    {
        var (db, store) = await NewStoreAsync();
        using (db)
        {
            await ReadShapeScenarios.IncludesPastTheParameterLimit(store);
        }
    }

    [Test]
    public async Task Distinct_values_of_two_members()
    {
        var (db, store) = await NewStoreAsync();
        using (db)
        {
            await ReadShapeScenarios.DistinctOfTwoMembers(store);
        }
    }

    [Test]
    public async Task Explain_orders_on_whether_the_column_is_null_and_names_the_shaped_include()
    {
        using var db = await NewSchemaAsync();
        var plan = ((eQuantic.Core.Data.Repository.Read.IExplainableRepository<ShapeRun>)db.Resolve<IAsyncRepository<ShapeRun, Guid>>())
            .Explain(new QueryOptions<ShapeRun>()
                .OrderBy(x => x.Name, NullsOrder.First)
                .Include(x => x.Steps, steps => steps.OrderByDescending(s => s.StartedAt).Take(3)));

        Assert.That(plan.Statement, Does.Contain("ORDER BY CASE WHEN [name] IS NULL THEN 0 ELSE 1 END, [name] ASC"),
            "SQL Server has no NULLS FIRST: the order of an is-null expression places them");
        Assert.That(plan.Notes, Has.Some.Contains("ROW_NUMBER()"));
    }
}
