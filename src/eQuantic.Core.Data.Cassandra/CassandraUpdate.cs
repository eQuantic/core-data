using System.Linq.Expressions;
using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>
///     Renders the dialect-agnostic <see cref="UpdateAssignment" /> model (produced by the core
///     <see cref="UpdateInterpreter" />) into a CQL <c>SET</c> assignment list with bound values: constant
///     assignments become <c>col = ?</c> and collection updates become the native read-modify-write forms
///     (<c>col = col + ?</c>, <c>col = ? + col</c>, <c>col = col - ?</c>) — atomic per column in Cassandra.
///     Numeric read-modify-writes need a counter column, which the model does not declare yet, so they are
///     rejected with the reason.
/// </summary>
internal static class CassandraUpdate
{
    public static (string Set, object?[] Values) Build<TEntity>(Expression<Func<TEntity, TEntity>> updateFactory)
    {
        var assignments = new List<string>();
        var values = new List<object?>();

        foreach (var assignment in UpdateInterpreter.Interpret(updateFactory))
        {
            switch (assignment)
            {
                case SetAssignment set:
                    assignments.Add($"{set.Name} = ?");
                    values.Add(set.Value);
                    break;
                case CollectionAddAssignment add when add.Unique && !add.IsSetMember():
                    throw new NotSupportedException(
                        $"CQL '+' on a list appends duplicates; Union needs a set column and '{add.Name}' is not one.");
                case CollectionAddAssignment { Prepend: true } add:
                    assignments.Add($"{add.Name} = ? + {add.Name}");
                    values.Add(add.ToTypedCollection());
                    break;
                case CollectionAddAssignment add:
                    assignments.Add($"{add.Name} = {add.Name} + ?");
                    values.Add(add.ToTypedCollection());
                    break;
                case CollectionRemoveAssignment remove:
                    assignments.Add($"{remove.Name} = {remove.Name} - ?");
                    values.Add(remove.ToTypedCollection());
                    break;
                case IncrementAssignment increment:
                    throw new NotSupportedException(
                        $"A numeric read-modify-write on '{increment.Name}' needs a Cassandra counter column, which the model " +
                        "does not declare yet; load the rows and Modify them instead.");
                case MultiplyAssignment multiply:
                    throw new NotSupportedException(
                        $"CQL cannot multiply in an UPDATE ('{multiply.Name}'); load the rows and Modify them instead.");
                default:
                    throw new NotSupportedException($"The Cassandra provider cannot render the assignment '{assignment.GetType().Name}'.");
            }
        }

        return (string.Join(", ", assignments), values.ToArray());
    }
}
