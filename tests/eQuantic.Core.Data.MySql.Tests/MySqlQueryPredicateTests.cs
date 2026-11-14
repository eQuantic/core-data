using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.MySql.Tests;

/// <summary>
///     The query predicates against a real MySQL: filters across relations as correlated <c>EXISTS</c> in reads,
///     counts and set-based writes, text matched with <c>LOWER()</c> on both sides, and <c>Contains</c> over an array or a
///     list as <c>IN</c>.
/// </summary>
[TestFixture]
public sealed class MySqlQueryPredicateTests : MySqlIntegrationTest
{
    private MySqlTestDatabase _db = null!;

    [SetUp]
    public async Task CreateDatabase() => _db = await NewSchemaAsync();

    [TearDown]
    public void DropDatabase() => _db?.Dispose();

    private IServiceScope NewScope() => _db.NewScope();

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
}
