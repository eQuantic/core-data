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
/// </summary>
internal static class CosmosPatch
{
    public static IReadOnlyList<PatchOperation> Build<TEntity>(Expression<Func<TEntity, TEntity>> updateFactory)
    {
        var operations = new List<PatchOperation>();

        foreach (var assignment in UpdateInterpreter.Interpret(updateFactory))
        {
            var path = "/" + CosmosNaming.CamelCase(assignment.Name);
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
