using eQuantic.Core.Data.Evolution;
using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.MySql.Extensions;
using eQuantic.Core.Data.MySql.Repository;
using eQuantic.Core.Data.Repository;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MariaDb;

namespace eQuantic.Core.Data.MySql.Tests;

/// <summary>
///     Exercises the MariaDB dialect against a real server: the same MySQL engine and driver, plus
///     <c>INSERT … RETURNING</c> — generated keys are read back into the entities, the capability the MySQL
///     dialect honestly rejects. The fixture boots its own container and shares one schema across its tests.
/// </summary>
[TestFixture]
public sealed class MariaDbTests
{
    private MariaDbContainer? _container;
    private ServiceProvider? _provider;
    private IServiceScope? _scope;
    private Exception? _startupError;
    private readonly CommandCapture _commands = new();

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        try
        {
            _container = new MariaDbBuilder().Build();
            await _container.StartAsync();

            var services = new ServiceCollection();
            // The bulk-load scenario rides LOAD DATA LOCAL INFILE, which the client opts into here.
            var connectionString = new MySqlConnector.MySqlConnectionStringBuilder(_container.GetConnectionString())
            {
                AllowLoadLocalInfile = true,
            }.ConnectionString;
            services.AddMariaDbDatabase(connectionString, model => TestSchema.ConfigureMariaDb(ReadShapesSchema.Configure(model)));
            services.AddMySqlRepositories();
            services.AddMySqlMigrations(typeof(MariaDbTests).Assembly);
            _commands.Register(services);
            _provider = services.BuildServiceProvider();
            _scope = _provider.CreateScope();
            await _scope.ServiceProvider.GetRequiredService<IMigrationRunner>().RunAsync();
        }
        catch (Exception ex)
        {
            _startupError = ex;
        }
    }

    [OneTimeTearDown]
    public async Task StopAsync()
    {
        _scope?.Dispose();
        _provider?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [SetUp]
    public void RequireDocker()
    {
        if (_startupError is not null)
        {
            Assert.Ignore($"MariaDB test container is unavailable (Docker required): {_startupError.Message}");
        }
    }

    private IServiceScope NewScope() => _provider!.CreateScope();

    // ---------------------------------------------------------------- write outcomes (the same scenarios as MySQL)

    [Test]
    public Task An_upsert_inserts_then_updates() => WriteOutcomeScenarios.UpsertInsertsThenUpdates(NewScope);

    [Test]
    public Task An_upsert_on_a_compound_unique_key_applies_the_update_and_keeps_the_first_key() =>
        WriteOutcomeScenarios.UpsertOnCompoundUniqueKeyWithUpdate(NewScope);

    [Test]
    public Task Ten_concurrent_upserts_of_one_target_leave_one_row_and_none_fails() => WriteOutcomeScenarios.UpsertRace(NewScope);

    [Test]
    public Task An_upsert_takes_store_generated_values_on_insert_and_never_writes_them_on_update() =>
        WriteOutcomeScenarios.UpsertKeepsStoreGeneratedMembers(NewScope);

    [Test]
    public Task A_conflict_filter_restricts_the_target() => WriteOutcomeScenarios.UpsertWithConflictFilter(NewScope);

    [Test]
    public Task Inserting_many_skips_the_rows_already_there() => WriteOutcomeScenarios.InsertManySkipsDuplicates(NewScope);

    [Test]
    public Task Inserting_many_without_skipping_fails_on_a_taken_key_and_writes_nothing() =>
        WriteOutcomeScenarios.InsertManyWithoutSkippingFails(NewScope);

    [Test]
    public Task A_staged_insert_of_a_taken_key_fails_the_commit_atomically() =>
        WriteOutcomeScenarios.StagedInsertOfTakenKeyFailsTheCommit(NewScope);

    [Test]
    public Task A_unique_index_other_than_the_key_is_named_on_every_write_path() => WriteOutcomeScenarios.UniqueIndexIsNamed(NewScope);

    [Test]
    public Task Two_concurrent_compare_and_sets_match_once() => WriteOutcomeScenarios.CompareAndSet(NewScope);

    [Test]
    public Task A_set_based_write_counts_the_rows_it_matched() => WriteOutcomeScenarios.MatchedNotModified(NewScope);

    [Test]
    public Task Two_concurrent_increments_read_their_own_values() => WriteOutcomeScenarios.IncrementAndRead(NewScope);

    [Test]
    public Task A_member_scoped_modify_leaves_the_other_members() => WriteOutcomeScenarios.PartialModifyKeepsOtherMembers(NewScope);

    [Test]
    public Task A_bulk_load_of_a_taken_key_fails() => WriteOutcomeScenarios.BulkInsertOfTakenKeyFails(NewScope);

    [Test]
    public Task An_upsert_on_a_generated_key_updates_the_row_holding_it() => WriteOutcomeScenarios.UpsertOnGeneratedKey(NewScope);

    [Test]
    public Task A_conflict_filter_with_a_value_holds_past_the_prepared_statement_threshold() =>
        WriteOutcomeScenarios.ConflictFilterWithAValue(NewScope);

    [Test]
    public Task Two_concurrent_upserts_in_explicit_transactions_both_succeed() =>
        WriteOutcomeScenarios.UpsertsInExplicitTransactions(NewScope);

    [Test]
    public Task Ten_concurrent_upserts_of_a_missing_target_in_explicit_transactions_all_succeed() =>
        WriteOutcomeScenarios.UpsertRaceInExplicitTransactions(NewScope);

    [Test]
    public Task Ten_concurrent_upserts_of_one_target_written_in_different_cases_take_one_lock() =>
        WriteOutcomeScenarios.UpsertRaceInExplicitTransactions(NewScope, mixedCase: true);

    [Test]
    public Task An_upsert_target_lock_is_the_targets_alone_and_ends_with_its_transaction() =>
        WriteOutcomeScenarios.UpsertTargetLocksAreTheTargetsAlone(NewScope);

    [Test]
    public Task A_bulk_load_of_a_taken_key_inside_a_transaction_writes_nothing() =>
        WriteOutcomeScenarios.BulkInsertOfTakenKeyInsideATransaction(NewScope);

    [Test]
    public Task Keys_first_writes_past_the_parameter_limit_bind_within_it() =>
        WriteOutcomeScenarios.KeysFirstWritesPastTheParameterLimit(NewScope, _commands,
            _scope!.ServiceProvider.GetRequiredService<eQuantic.Core.Data.Relational.SqlDialect>().MaxParameters);

    // ---------------------------------------------------------------- query predicates (the same scenarios as MySQL)

    [Test]
    public Task Any_with_a_condition() => QueryPredicateScenarios.AnyWithACondition(NewScope);

    [Test]
    public Task A_reference_member_in_a_set_based_delete() => QueryPredicateScenarios.ReferenceMemberInSetBasedDelete(NewScope);

    [Test]
    public Task No_related_row() => QueryPredicateScenarios.NoRelatedRow(NewScope);

    [Test]
    public void A_relation_test_reading_the_entity_is_refused() => QueryPredicateScenarios.CorrelationIsRefused(NewScope);

    [Test]
    public void Explain_names_the_correlated_exists() => QueryPredicateScenarios.ExplainNamesTheMechanism(NewScope);

    [Test]
    public Task Text_is_matched_without_regard_to_case() => QueryPredicateScenarios.TextWithoutRegardToCase(NewScope);

    [Test]
    public Task An_array_and_a_list_are_tested_with_Contains() => QueryPredicateScenarios.ArrayAndListContains(NewScope);

    [Test]
    public Task A_self_referencing_relation_in_set_based_writes() => QueryPredicateScenarios.SelfReferencingRelationInSetBasedWrites(NewScope);

    [Test]
    public Task A_self_referencing_relation_inside_a_transaction() => QueryPredicateScenarios.SelfReferencingRelationInsideATransaction(NewScope);

    [Test]
    public Task Labels_compare_and_order_by_their_members_values() => QueryPredicateScenarios.LabelsCompareAndOrderByValue(NewScope);

    [Test]
    public Task A_negated_text_equality_keeps_the_rows_whose_member_is_null() => QueryPredicateScenarios.NegatedTextEqualityKeepsNull(NewScope);

    [Test]
    public void A_relation_condition_in_a_delegate_is_refused() => QueryPredicateScenarios.RelationConditionInADelegateIsRefused(NewScope);

    [Test]
    public Task A_minimum_and_a_maximum_of_labels_answer_by_value() => QueryPredicateScenarios.LabelAggregatesAnswerByValue(NewScope);

    [Test]
    public Task A_group_keyed_by_labels_has_by_value() => QueryPredicateScenarios.LabelGroupsKeyAndHaveByValue(NewScope);

    // ---------------------------------------------------------------- generated keys

    [Test]
    public async Task Generated_keys_are_read_back_with_returning()
    {
        var repo = _scope!.ServiceProvider.GetRequiredService<IAsyncRepository<Ticket, long>>();
        var uow = _scope.ServiceProvider.GetRequiredService<MySqlDefaultUnitOfWork>();

        var first = new Ticket { Label = "one" };
        var second = new Ticket { Label = "two" };
        await repo.AddAsync(first);
        await repo.AddAsync(second);
        await uow.CommitAsync();

        Assert.That(first.Id, Is.GreaterThan(0), "RETURNING backfilled the identity — the capability MySQL rejects");
        Assert.That(second.Id, Is.GreaterThan(first.Id));
        Assert.That((await repo.GetAsync(second.Id))!.Label, Is.EqualTo("two"));
    }

    [Test]
    public async Task Round_trip_and_pushdown_run_on_the_shared_engine()
    {
        var repo = _scope!.ServiceProvider.GetRequiredService<IAsyncRepository<SaleOrder, Guid>>();
        var uow = _scope.ServiceProvider.GetRequiredService<MySqlDefaultUnitOfWork>();

        var marker = Guid.NewGuid().ToString("N")[..12];
        await repo.AddAsync(new SaleOrder
        {
            Id = Guid.NewGuid(),
            Customer = marker,
            Total = 12.5m,
            Status = "open",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await uow.CommitAsync();

        var found = await repo.GetFilteredAsync(x => x.Customer == marker || x.Total > 9999m);
        Assert.That(found.Single().Total, Is.EqualTo(12.5m), "native OR pushdown over the MariaDB dialect");
    }

    private IAsyncRepository<RunRecord, Guid> Runs => _scope!.ServiceProvider.GetRequiredService<IAsyncRepository<RunRecord, Guid>>();

    private Task Commit() => _scope!.ServiceProvider.GetRequiredService<MySqlDefaultUnitOfWork>().CommitAsync();

    private async Task<object?> Scalar(string sql)
    {
        await using var command = _scope!.ServiceProvider.GetRequiredService<System.Data.Common.DbDataSource>().CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }

    [Test]
    public Task Enums_are_stored_as_labels() => NativeTypesScenarios.EnumsAreStoredAsLabels(Runs, Commit, Scalar);

    [Test]
    public Task Json_members_round_trip() => NativeTypesScenarios.JsonMembersRoundTrip(Runs, Commit);

    [Test]
    public Task Store_generated_members_are_read_back_with_returning() =>
        NativeTypesScenarios.StoreGeneratedMembersAreTheStores(Runs, Commit, Scalar);

    [Test]
    public async Task A_generated_key_is_read_back_with_the_store_generated_members_after_it()
    {
        // Receipt's table is MariaDB's alone: MySQL cannot read a generated key back, so the shared setup leaves it out.
        await _scope!.ServiceProvider.GetRequiredService<IMigrationExecutor>()
            .ApplyAsync([new EnsureCollectionOperation(typeof(Receipt))]);
        var repo = _scope.ServiceProvider.GetRequiredService<IAsyncRepository<Receipt, long>>();

        var first = new Receipt { Note = "first" };
        var second = new Receipt { Note = "second" };
        await repo.AddAsync(first);
        await repo.AddAsync(second);
        await Commit();

        Assert.That(first.Id, Is.GreaterThan(0L));
        Assert.That(second.Number, Is.Not.EqualTo(first.Number));
        Assert.That(first.Number, Is.GreaterThan(0L), "the sequence object numbered it");
        Assert.That(first.IssuedAt, Is.Not.EqualTo(default(DateTime)));
        Assert.That((await repo.GetAsync(second.Id))!.Number, Is.EqualTo(second.Number), "each value went to its own member");
    }

    [Test]
    public async Task A_schema_the_engine_created_reports_no_drift()
    {
        // Receipt's table is created the same way as below, and creating it again changes nothing, so the order the
        // tests run in does not matter.
        await _scope!.ServiceProvider.GetRequiredService<IMigrationExecutor>()
            .ApplyAsync([new EnsureCollectionOperation(typeof(Receipt))]);
        var source = _scope.ServiceProvider.GetRequiredService<IDatabaseSnapshotSource>();

        var report = DriftComparer.Compare(source.Expect(), await source.ObserveAsync());

        Assert.That(report.Findings, Is.Empty,
            "labels in an ENUM, JSON as LONGTEXT, a sequence's default and the clock's must read as the dialect wrote them");
    }

    [Test]
    public async Task Members_added_as_store_generated_are_filled_in_the_rows_already_there()
    {
        // A table of its own in the fixture's database, which the shared model does not map. As root: MariaDB 10.10
        // refuses the ALTER that fills a column defaulting to a sequence's next value ("ALTER command denied … for
        // table stamps_number_seq") to a user holding ALL PRIVILEGES on the database, and runs it for root.
        var root = new MySqlConnector.MySqlConnectionStringBuilder(_container!.GetConnectionString()) { UserID = "root" }
            .ConnectionString;
        var services = new ServiceCollection();
        services.AddMariaDbDatabase(root, StoreGeneratedAdditionScenario.Configure);
        await StoreGeneratedAdditionScenario.RunAsync(root, services);
    }

    private ShapeStore Shapes => new(type => _scope!.ServiceProvider.GetRequiredService(type), Commit, _commands);

    [Test]
    public Task An_include_is_filtered_ordered_and_limited_per_parent() => ReadShapeScenarios.TheLastThreeNewestFirst(Shapes);

    [Test]
    public Task A_relation_is_counted_not_loaded() => ReadShapeScenarios.CountedNotLoaded(Shapes);

    [Test]
    public Task Nulls_are_ordered_first_or_last() => ReadShapeScenarios.NullsFirstOrLast(Shapes);

    [Test]
    public Task A_page_is_read_without_its_total() => ReadShapeScenarios.TakeTenWithoutTotal(Shapes);

    [Test]
    public Task Distinct_values_of_two_members() => ReadShapeScenarios.DistinctOfTwoMembers(Shapes);

    [Test]
    public Task A_skip_without_a_take_returns_every_row_after_it() => ReadShapeScenarios.SkipWithoutTakeAndTakeWithoutSkip(Shapes);

    [Test]
    public Task A_take_of_zero_returns_nothing_without_asking_the_store() => ReadShapeScenarios.TakeZero(Shapes);

    [Test]
    public Task Includes_of_more_parents_than_one_statement_binds() => ReadShapeScenarios.IncludesPastTheParameterLimit(Shapes);
}
