using System.Linq.Expressions;
using Microsoft.Azure.Cosmos;

namespace eQuantic.Core.Data.CosmosDb;

/// <summary>
///     Translates a member-initialisation update (<c>x =&gt; new TEntity { Status = "Closed", ... }</c>) into
///     Cosmos <see cref="PatchOperation" />s (one <c>Set</c> per assigned member, camelCase path). Assignments
///     that reference the entity (computed updates such as <c>x =&gt; new E { N = x.N + 1 }</c>) are not supported.
/// </summary>
internal static class CosmosPatch
{
    public static IReadOnlyList<PatchOperation> BuildSet<TEntity>(Expression<Func<TEntity, TEntity>> updateFactory)
    {
        if (updateFactory.Body is not MemberInitExpression memberInit)
        {
            throw new NotSupportedException(
                "UpdateMany expects a member-initialisation update, e.g. x => new " + typeof(TEntity).Name + " { Status = \"Closed\" }.");
        }

        var parameter = updateFactory.Parameters[0];
        var operations = new List<PatchOperation>();

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
                    "(e.g. x => new E { N = x.N + 1 }) are not supported — load the documents and Modify them instead.");
            }

            operations.Add(PatchOperation.Set("/" + CosmosNaming.CamelCase(assignment.Member.Name), Evaluate(assignment.Expression)));
        }

        if (operations.Count == 0)
        {
            throw new NotSupportedException("The UpdateMany update assigns no fields.");
        }

        return operations;
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
