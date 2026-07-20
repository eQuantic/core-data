using System.Linq.Expressions;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>
///     Unit tests for partition-key inference — pure expression analysis, no emulator required. Proves a filter
///     that pins the partition key scopes the query to a single partition.
/// </summary>
[TestFixture]
public sealed class CosmosPartitionKeyInferenceTests
{
    [Test]
    public void Infers_the_partition_key_from_an_equality()
    {
        Expression<Func<CosmosProduct, bool>> filter = x => x.Category == "Books";

        var partitionKey = CosmosPartitionKeyInference.Infer("/category", filter);

        Assert.That(partitionKey, Is.Not.Null);
        Assert.That(partitionKey!.Value.ToString(), Is.EqualTo(new PartitionKey("Books").ToString()));
    }

    [Test]
    public void Infers_from_a_captured_variable_via_partial_evaluation()
    {
        var category = "Books";
        Expression<Func<CosmosProduct, bool>> filter = x => x.Category == category;

        var partitionKey = CosmosPartitionKeyInference.Infer("/category", filter);

        Assert.That(partitionKey!.Value.ToString(), Is.EqualTo(new PartitionKey("Books").ToString()));
    }

    [Test]
    public void Infers_from_a_reversed_equality()
    {
        Expression<Func<CosmosProduct, bool>> filter = x => "Books" == x.Category;

        var partitionKey = CosmosPartitionKeyInference.Infer("/category", filter);

        Assert.That(partitionKey!.Value.ToString(), Is.EqualTo(new PartitionKey("Books").ToString()));
    }

    [Test]
    public void Infers_from_an_and_chain()
    {
        Expression<Func<CosmosProduct, bool>> filter = x => x.Quantity > 0 && x.Category == "Books";

        var partitionKey = CosmosPartitionKeyInference.Infer("/category", filter);

        Assert.That(partitionKey!.Value.ToString(), Is.EqualTo(new PartitionKey("Books").ToString()));
    }

    [Test]
    public void Infers_a_numeric_partition_key()
    {
        Expression<Func<CosmosProduct, bool>> filter = x => x.Quantity == 5;

        var partitionKey = CosmosPartitionKeyInference.Infer("/quantity", filter);

        Assert.That(partitionKey!.Value.ToString(), Is.EqualTo(new PartitionKey(5.0).ToString()));
    }

    [Test]
    public void Does_not_infer_across_an_or()
    {
        Expression<Func<CosmosProduct, bool>> filter = x => x.Category == "Books" || x.Category == "Food";

        Assert.That(CosmosPartitionKeyInference.Infer("/category", filter), Is.Null);
    }

    [Test]
    public void Does_not_infer_when_the_partition_key_is_not_filtered()
    {
        Expression<Func<CosmosProduct, bool>> filter = x => x.Quantity > 0;

        Assert.That(CosmosPartitionKeyInference.Infer("/category", filter), Is.Null);
    }

    [Test]
    public void Does_not_infer_a_hierarchical_partition_key()
    {
        Expression<Func<CosmosProduct, bool>> filter = x => x.Category == "Books";

        Assert.That(CosmosPartitionKeyInference.Infer("/tenant/category", filter), Is.Null);
    }

    [Test]
    public void Returns_null_when_there_is_no_filter()
    {
        Assert.That(CosmosPartitionKeyInference.Infer("/category", (Expression?)null), Is.Null);
    }
}
