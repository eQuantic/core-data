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
    public static string Render<TEntity>(SqlDialect dialect, RelationalEntityConfiguration configuration,
        Expression<Func<TEntity, TEntity>> updateFactory, List<object?> parameters)
    {
        var fragments = new List<string>();

        foreach (var assignment in UpdateInterpreter.Interpret(updateFactory))
        {
            var column = dialect.Quote((configuration.ColumnFor(assignment.Name)
                                        ?? throw new NotSupportedException(
                                            $"'{configuration.EntityType.Name}' has no mapped member '{assignment.Name}'.")).Name);
            switch (assignment)
            {
                case SetAssignment set:
                    // A converted member's assigned value binds as its stored form.
                    fragments.Add($"{column} = {Bind(configuration.ColumnFor(assignment.Name)?.Store(set.Value) ?? set.Value)}");
                    break;
                case IncrementAssignment increment:
                    fragments.Add($"{column} = {column} + {Bind(increment.Delta)}");
                    break;
                case MultiplyAssignment multiply:
                    fragments.Add($"{column} = {column} * {Bind(multiply.Factor)}");
                    break;
                case CollectionAddAssignment add:
                    fragments.Add(dialect.CollectionMutation(column, Bind(add.ToTypedCollection()), remove: false, add.Prepend));
                    break;
                case CollectionRemoveAssignment remove:
                    fragments.Add(dialect.CollectionMutation(column, Bind(remove.ToTypedCollection()), remove: true, prepend: false));
                    break;
                default:
                    throw new NotSupportedException($"The relational engine cannot render the assignment '{assignment.GetType().Name}'.");
            }
        }

        return string.Join(", ", fragments);

        string Bind(object? value)
        {
            parameters.Add(dialect.BindValue(value));
            return "@p" + (parameters.Count - 1);
        }
    }
}
