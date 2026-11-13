using eQuantic.Core.Data.Migration;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.MySql.Tests;

/// <summary>The write-outcome tables (MySQL has no filtered indexes).</summary>
[Migration("MySQL write outcomes", 2026, 1, 1, 0, 0, 2)]
public sealed class WriteOutcomeMigration : Data.Migration.Migration
{
    /// <inheritdoc />
    public override void Up(IMigrationBuilder migration) => WriteOutcomeSchema.Migrate(migration, partialIndexes: false);
}

/// <summary>
///     Proves, against a real MySQL, upserts (a locking read in a transaction, then the insert or the update), inserts skipping duplicates (row by row, discarding only duplicate-key errors), <c>DuplicateKeyException</c> on every write path, matched counts,
///     returning updates and member-scoped modifies.
/// </summary>
[TestFixture]
public sealed class MySqlWriteOutcomeTests : MySqlIntegrationTest
{
    private MySqlTestDatabase _db = null!;

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
}
