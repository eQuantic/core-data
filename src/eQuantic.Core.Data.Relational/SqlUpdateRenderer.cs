using System.Linq.Expressions;
using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Relational;

/// <summary>
///     Renders the dialect-agnostic <see cref="UpdateAssignment" /> model into a SQL <c>SET</c> list. SQL applies
///     every shape atomically in place: constants, numeric read-modify-writes (<c>col = col + @p</c>,
///     <c>col = col * @p</c>) and — where the dialect has collection columns — collection add/remove.
/// </summary>
internal static class SqlUpdateRenderer
{
    /// <summary>Renders the update's <c>SET</c> fragments.</summary>
    /// <param name="dialect">The dialect.</param>
    /// <param name="configuration">The entity's mapping.</param>
    /// <param name="updateFactory">The update.</param>
    /// <param name="parameters">The bound values, appended to.</param>
    /// <param name="existingRow">
    ///     How an assignment that reads the row refers to it (<c>col = existing.col + 1</c>) — an upsert's update has the
    ///     proposed row in scope too, and PostgreSQL calls an unqualified column there ambiguous; <c>null</c> leaves it
    ///     unqualified.
    /// </param>
    public static List<string> Fragments<TEntity>(SqlDialect dialect, RelationalEntityConfiguration configuration,
        Expression<Func<TEntity, TEntity>> updateFactory, List<object?> parameters, string? existingRow = null)
    {
        var fragments = new List<string>();

        foreach (var assignment in UpdateInterpreter.Interpret(updateFactory))
        {
            var column = dialect.Quote((configuration.ColumnFor(assignment.Name)
                                        ?? throw new NotSupportedException(
                                            $"'{configuration.EntityType.Name}' has no mapped member '{assignment.Name}'.")).Name);
            var current = existingRow is null ? column : existingRow + "." + column;
            switch (assignment)
            {
                case SetAssignment set:
                    // A converted member's assigned value binds as its stored form.
                    fragments.Add($"{column} = {Bind(configuration.ColumnFor(assignment.Name)?.Store(set.Value) ?? set.Value)}");
                    break;
                case IncrementAssignment increment:
                    fragments.Add($"{column} = {current} + {Bind(increment.Delta)}");
                    break;
                case MultiplyAssignment multiply:
                    fragments.Add($"{column} = {current} * {Bind(multiply.Factor)}");
                    break;
                case CollectionAddAssignment add:
                    fragments.Add(Assigned(column, dialect.CollectionMutation(current, Bind(add.ToTypedCollection()), remove: false, add.Prepend), current));
                    break;
                case CollectionRemoveAssignment remove:
                    fragments.Add(Assigned(column, dialect.CollectionMutation(current, Bind(remove.ToTypedCollection()), remove: true, prepend: false), current));
                    break;
                default:
                    throw new NotSupportedException($"The relational engine cannot render the assignment '{assignment.GetType().Name}'.");
            }
        }

        return fragments;

        string Bind(object? value)
        {
            parameters.Add(dialect.BindValue(value));
            return "@p" + (parameters.Count - 1);
        }
    }

    /// <summary>Renders the update into a SQL <c>SET</c> list.</summary>
    public static string Render<TEntity>(SqlDialect dialect, RelationalEntityConfiguration configuration,
        Expression<Func<TEntity, TEntity>> updateFactory, List<object?> parameters) =>
        string.Join(", ", Fragments(dialect, configuration, updateFactory, parameters));

    /// <summary>A collection mutation rendered over a qualified read keeps its target unqualified.</summary>
    private static string Assigned(string column, string fragment, string current) =>
        current == column || !fragment.StartsWith(current + " =", StringComparison.Ordinal)
            ? fragment
            : column + fragment[current.Length..];
}
