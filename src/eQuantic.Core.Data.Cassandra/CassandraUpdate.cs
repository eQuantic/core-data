using System.Linq.Expressions;

namespace eQuantic.Core.Data.Cassandra;

/// <summary>
///     Translates a member-initialisation update (<c>x =&gt; new TEntity { Status = "Closed", ... }</c>) into a CQL
///     <c>SET</c> assignment list with bound values. Assignments that reference the entity (computed updates) are
///     not supported.
/// </summary>
internal static class CassandraUpdate
{
    public static (string Set, object?[] Values) BuildSet<TEntity>(Expression<Func<TEntity, TEntity>> updateFactory)
    {
        if (updateFactory.Body is not MemberInitExpression memberInit)
        {
            throw new NotSupportedException(
                "UpdateMany expects a member-initialisation update, e.g. x => new " + typeof(TEntity).Name + " { Status = \"Closed\" }.");
        }

        var parameter = updateFactory.Parameters[0];
        var assignments = new List<string>();
        var values = new List<object?>();

        foreach (var binding in memberInit.Bindings)
        {
            if (binding is not MemberAssignment assignment)
            {
                throw new NotSupportedException(
                    $"Only member assignments are supported in an UpdateMany update; got '{binding.BindingType}'.");
            }

            if (ReferencesParameter(assignment.Expression, parameter))
            {
                throw new NotSupportedException(
                    $"The assignment to '{assignment.Member.Name}' references the entity; computed set-based updates " +
                    "are not supported — load the rows and Modify them instead.");
            }

            assignments.Add($"{assignment.Member.Name} = ?");
            values.Add(Evaluate(assignment.Expression));
        }

        if (assignments.Count == 0)
        {
            throw new NotSupportedException("The UpdateMany update assigns no columns.");
        }

        return (string.Join(", ", assignments), values.ToArray());
    }

    private static object? Evaluate(Expression expression) =>
        expression is ConstantExpression constant
            ? constant.Value
            : Expression.Lambda(expression).Compile().DynamicInvoke();

    private static bool ReferencesParameter(Expression expression, ParameterExpression parameter)
    {
        var finder = new ParameterFinder(parameter);
        finder.Visit(expression);
        return finder.Found;
    }

    private sealed class ParameterFinder(ParameterExpression parameter) : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == parameter)
            {
                Found = true;
            }

            return base.VisitParameter(node);
        }
    }
}
