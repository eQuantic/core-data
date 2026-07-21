using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb.Tests;

/// <summary>
///     Unit tests for <see cref="CosmosPatch" /> — pure rendering of the update IR to patch operations, no
///     emulator. Proves computed shapes map to the native atomic operations and inexpressible ones are rejected.
/// </summary>
[TestFixture]
public sealed class CosmosPatchTests
{
    [Test]
    public void Constant_assignment_renders_a_set()
    {
        var operations = CosmosPatch.Build<CosmosProduct>(x => new CosmosProduct { Name = "renamed" });

        Assert.That(operations, Has.Count.EqualTo(1));
        Assert.That(operations[0].OperationType, Is.EqualTo(PatchOperationType.Set));
        Assert.That(operations[0].Path, Is.EqualTo("/name"));
    }

    [Test]
    public void Member_plus_constant_renders_the_native_increment()
    {
        var operations = CosmosPatch.Build<CosmosProduct>(x => new CosmosProduct { Quantity = x.Quantity + 5 });

        Assert.That(operations, Has.Count.EqualTo(1));
        Assert.That(operations[0].OperationType, Is.EqualTo(PatchOperationType.Increment));
        Assert.That(operations[0].Path, Is.EqualTo("/quantity"));
    }

    [Test]
    public void Member_minus_constant_renders_a_negative_increment()
    {
        var operations = CosmosPatch.Build<CosmosProduct>(x => new CosmosProduct { Quantity = x.Quantity - 3 });

        Assert.That(operations[0].OperationType, Is.EqualTo(PatchOperationType.Increment));
    }

    [Test]
    public void Multiply_is_rejected_with_the_reason()
    {
        Assert.That(() => CosmosPatch.Build<CosmosProduct>(x => new CosmosProduct { Price = x.Price * 2m }),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("multiply"));
    }
}
