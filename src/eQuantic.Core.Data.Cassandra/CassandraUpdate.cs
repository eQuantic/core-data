using System.Linq.Expressions;
using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>
///     Renders the dialect-agnostic <see cref="UpdateAssignment" /> model (produced by the core
///     <see cref="UpdateInterpreter" />) into a CQL <c>SET</c> assignment list with bound values: constant
///     assignments become <c>col = ?</c> and collection updates become the native read-modify-write forms
///     (<c>col = col + ?</c>, <c>col = ? + col</c>, <c>col = col - ?</c>) — atomic per column in Cassandra.
///     Numeric read-modify-writes render as counter increments when the model declares the column with
///     <c>Counter(...)</c>, and are rejected with guidance otherwise.
/// </summary>
internal static class CassandraUpdate
{
    public static (string Set, object?[] Values) Build<TEntity>(CassandraEntityConfiguration configuration,
        Expression<Func<TEntity, TEntity>> updateFactory)
    {
        var assignments = new List<string>();
        var values = new List<object?>();

        foreach (var assignment in UpdateInterpreter.Interpret(updateFactory))
        {
            // The interpreter names CLR members; the CQL wants the stored column names.
            var column = configuration.ColumnFor(assignment.Name);
            switch (assignment)
            {
                case SetAssignment set when configuration.IsCounter(column):
                    throw new NotSupportedException(
                        $"A counter column cannot be set ('{set.Name}'); counters only move by increments (x => new ... {{ {set.Name} = x.{set.Name} + n }}).");
                case SetAssignment set:
                    assignments.Add($"{column} = ?");
                    values.Add(set.Value);
                    break;
                case IncrementAssignment increment when configuration.IsCounter(column):
                    assignments.Add($"{column} = {column} + ?");
                    values.Add(Convert.ToInt64(increment.Delta));
                    break;
                case CollectionAddAssignment add when add.Unique && !add.IsSetMember():
                    throw new NotSupportedException(
                        $"CQL '+' on a list appends duplicates; Union needs a set column and '{add.Name}' is not one.");
                case CollectionAddAssignment { Prepend: true } add:
                    assignments.Add($"{column} = ? + {column}");
                    values.Add(add.ToTypedCollection());
                    break;
                case CollectionAddAssignment add:
                    assignments.Add($"{column} = {column} + ?");
                    values.Add(add.ToTypedCollection());
                    break;
                case CollectionRemoveAssignment remove:
                    assignments.Add($"{column} = {column} - ?");
                    values.Add(remove.ToTypedCollection());
                    break;
                case IncrementAssignment increment:
                    throw new NotSupportedException(
                        $"A numeric read-modify-write on '{increment.Name}' needs a Cassandra counter column; declare it with " +
                        $"Counter(x => x.{increment.Name}) in the model, or load the rows and Modify them instead.");
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
