using eQuantic.Core.Data.Migration;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>The write-outcome tables, with the partial unique index PostgreSQL has.</summary>
[Migration("PostgreSQL write outcomes", 2026, 1, 1, 0, 0, 2)]
public sealed class WriteOutcomeMigration : Data.Migration.Migration
{
    /// <inheritdoc />
    public override void Up(IMigrationBuilder migration) => WriteOutcomeSchema.Migrate(migration, partialIndexes: true);
}

/// <summary>
///     Proves, against a real PostgreSQL, upserts (<c>INSERT … ON CONFLICT … RETURNING</c>), inserts skipping duplicates
///     (<c>ON CONFLICT DO NOTHING</c>), <c>DuplicateKeyException</c> on every write path, matched counts, returning updates
///     and member-scoped modifies.
/// </summary>
[TestFixture]
public sealed class PostgreSqlWriteOutcomeTests : PostgreSqlIntegrationTest
{
    private PostgreSqlTestDatabase _db = null!;

    [SetUp]
    public async Task CreateDatabase() => _db = await NewSchemaAsync();

    [TearDown]
    public void DropDatabase() => _db?.Dispose();

    private IServiceScope NewScope() => _db.NewScope();

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
    public Task A_conflict_filter_is_the_partial_unique_index_predicate() => WriteOutcomeScenarios.UpsertWithConflictFilter(NewScope);

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
}
