using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>
///     The query predicates against a real PostgreSQL: filters across relations as correlated <c>EXISTS</c> in reads,
///     counts and set-based writes, text matched with <c>lower()</c> on both sides or <c>ILIKE</c>, and <c>Contains</c> over an array or a
///     list as <c>IN</c>.
/// </summary>
[TestFixture]
public sealed class PostgreSqlQueryPredicateTests : PostgreSqlIntegrationTest
{
    private PostgreSqlTestDatabase _db = null!;

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
}
