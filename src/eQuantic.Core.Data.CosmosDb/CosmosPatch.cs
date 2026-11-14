using System.Globalization;
using System.Linq.Expressions;
using eQuantic.Core.Data.Query;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb;

/// <summary>
///     Renders the dialect-agnostic <see cref="UpdateAssignment" /> model (produced by the core
///     <see cref="UpdateInterpreter" />) into Cosmos <see cref="PatchOperation" />s: constant assignments become
///     <c>Set</c>, numeric read-modify-writes become the native <c>Increment</c>, and collection appends/prepends
///     become index-addressed <c>Add</c>s — all atomic on the server. Shapes the patch API cannot express
///     (multiply, remove-by-value, set union) are rejected with the reason.
///     <para>
///         Cosmos DB takes at most <see cref="MaxOperations" /> paths in one patch. A longer patch is applied by
///         <see cref="ApplyAsync{TEntity}" /> as several patches of the same item in one <see cref="TransactionalBatch" />
///         (the item's partition): the first carries the condition, and the batch applies whole or not at all.
///     </para>
/// </summary>
internal static class CosmosPatch
{
    /// <summary>The paths Cosmos DB takes in one patch operation.</summary>
    public const int MaxOperations = 10;

    /// <summary>The operations a patch of <paramref name="paths" /> paths takes in a transactional batch (<see cref="AddTo" />).</summary>
    /// <param name="paths">The paths of the patch.</param>
    public static int OperationsFor(int paths) => (paths + MaxOperations - 1) / MaxOperations;

    /// <summary>
    ///     Adds a patch of any number of paths to a batch, as patches of at most <see cref="MaxOperations" /> paths of the
    ///     same item, in order. The first carries the condition (the filter and the <c>_etag</c>): it is evaluated against
    ///     the item before any of the patch applies, and the batch's own isolation keeps every other writer out until the
    ///     last one has. The later patches carry none, since the first may have changed what the condition reads.
    /// </summary>
    /// <param name="batch">The batch of the item's partition.</param>
    /// <param name="id">The item's id.</param>
    /// <param name="operations">The patch, in order.</param>
    /// <param name="filterPredicate">The condition the item has to meet (<c>FROM root WHERE …</c>), or <c>null</c>.</param>
    /// <param name="ifMatchEtag">The <c>_etag</c> the item has to carry, or <c>null</c>.</param>
    public static void AddTo(TransactionalBatch batch, string id, IReadOnlyList<PatchOperation> operations,
        string? filterPredicate, string? ifMatchEtag)
    {
        var first = true;
        foreach (var chunk in operations.Chunk(MaxOperations))
        {
            batch.PatchItem(id, chunk, first
                ? new TransactionalBatchPatchItemRequestOptions { FilterPredicate = filterPredicate, IfMatchEtag = ifMatchEtag }
                : null);
            first = false;
        }
    }

    /// <summary>
    ///     Applies a patch of any number of paths to one item, atomically, and returns the item as the patch left it. Up
    ///     to <see cref="MaxOperations" /> paths it is one patch; beyond, it is several patches of the item in one
    ///     <see cref="TransactionalBatch" /> (<see cref="AddTo" />). Either way a refusal throws the
    ///     <see cref="CosmosException" /> a single patch throws, with its status: 412 when the condition did not hold, 404
    ///     when the item is gone, 409 when a unique key is taken, and nothing of the patch is written.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="container">The item's container.</param>
    /// <param name="id">The item's id.</param>
    /// <param name="partitionKey">The item's partition key.</param>
    /// <param name="operations">The patch, in order.</param>
    /// <param name="filterPredicate">The condition the item has to meet (<c>FROM root WHERE …</c>), or <c>null</c>.</param>
    /// <param name="ifMatchEtag">The <c>_etag</c> the item has to carry, or <c>null</c>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static async Task<TEntity> ApplyAsync<TEntity>(Container container, string id, PartitionKey partitionKey,
        IReadOnlyList<PatchOperation> operations, string? filterPredicate, string? ifMatchEtag, CancellationToken cancellationToken)
    {
        if (operations.Count <= MaxOperations)
        {
            var response = await container.PatchItemAsync<TEntity>(id, partitionKey, operations,
                new PatchItemRequestOptions { FilterPredicate = filterPredicate, IfMatchEtag = ifMatchEtag }, cancellationToken)
                .ConfigureAwait(false);
            return response.Resource;
        }

        var batch = container.CreateTransactionalBatch(partitionKey);
        AddTo(batch, id, operations, filterPredicate, ifMatchEtag);
        using var result = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccessStatusCode)
        {
            // The operation that failed carries the reason; the others answer 424 (failed dependency).
            var failed = result.FirstOrDefault(operation => !operation.IsSuccessStatusCode
                                                            && operation.StatusCode != System.Net.HttpStatusCode.FailedDependency);
            var status = failed?.StatusCode ?? result.StatusCode;
            throw new CosmosException(
                $"The patch of '{id}' ({operations.Count} paths in {result.Count} operations of one transactional batch) " +
                $"failed with status {status}: {result.ErrorMessage}",
                status, 0, result.ActivityId, result.RequestCharge);
        }

        return result.GetOperationResultAtIndex<TEntity>(result.Count - 1).Resource;
    }

    public static IReadOnlyList<PatchOperation> Build<TEntity>(Expression<Func<TEntity, TEntity>> updateFactory)
    {
        var operations = new List<PatchOperation>();

        foreach (var assignment in UpdateInterpreter.Interpret(updateFactory))
        {
            var path = "/" + CosmosNaming.StoredName(assignment.Member);
            switch (assignment)
            {
                case SetAssignment set:
                    operations.Add(PatchOperation.Set(path, set.Value));
                    break;
                case IncrementAssignment increment:
                    operations.Add(Increment(path, increment.Delta));
                    break;
                case CollectionAddAssignment { Unique: false, Prepend: false } add:
                    foreach (var item in add.Items)
                    {
                        operations.Add(PatchOperation.Add(path + "/-", item));
                    }

                    break;
                case CollectionAddAssignment { Unique: false, Prepend: true } add:
                    for (var index = add.Items.Count - 1; index >= 0; index--)
                    {
                        operations.Add(PatchOperation.Add(path + "/0", add.Items[index]));
                    }

                    break;
                case CollectionAddAssignment unique:
                    throw new NotSupportedException(
                        $"Cosmos patch has no set semantics (Union on '{unique.Name}'); load the documents and Modify them instead.");
                case MultiplyAssignment multiply:
                    throw new NotSupportedException(
                        $"Cosmos patch cannot multiply ('{multiply.Name}'); load the documents and Modify them instead.");
                case CollectionRemoveAssignment remove:
                    throw new NotSupportedException(
                        $"Cosmos patch removes array elements by index, not by value ('{remove.Name}'); load the documents and Modify them instead.");
                default:
                    throw new NotSupportedException($"The Cosmos provider cannot render the assignment '{assignment.GetType().Name}'.");
            }
        }

        return operations;
    }

    /// <summary>The native increment: integral deltas as <c>long</c>, floating/decimal deltas as <c>double</c>.</summary>
    private static PatchOperation Increment(string path, object delta) => delta switch
    {
        sbyte or byte or short or ushort or int or uint or long =>
            PatchOperation.Increment(path, Convert.ToInt64(delta, CultureInfo.InvariantCulture)),
        _ => PatchOperation.Increment(path, Convert.ToDouble(delta, CultureInfo.InvariantCulture)),
    };
}
