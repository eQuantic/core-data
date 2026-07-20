using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace eQuantic.Core.Data.MongoDb;

/// <summary>
///     Translates a member-initialisation update (<c>x =&gt; new TEntity { Status = "Closed", ... }</c>) into a
///     MongoDB <c>$set</c>. Each assigned member becomes a <c>$set</c> field whose stored name and value are
///     resolved through the class map (via <see cref="MongoFieldNames" />). Assignments that reference the entity
///     (computed updates such as <c>x =&gt; new E { N = x.N + 1 }</c>) are not yet supported.
/// </summary>
internal static class MongoUpdate
{
    public static UpdateDefinition<TEntity> BuildSet<TEntity>(Expression<Func<TEntity, TEntity>> updateFactory)
    {
        if (updateFactory.Body is not MemberInitExpression memberInit)
        {
            throw new NotSupportedException(
                "UpdateMany expects a member-initialisation update, e.g. x => new " + typeof(TEntity).Name + " { Status = \"Closed\" }.");
        }

        var parameter = updateFactory.Parameters[0];
        var set = new BsonDocument();

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
                    "(e.g. x => new E { N = x.N + 1 }) are not yet supported — use Modify on loaded documents instead.");
            }

            set.Add(
                MongoFieldNames.Resolve(typeof(TEntity), assignment.Member),
                MongoFieldNames.Serialize(typeof(TEntity), assignment.Member, Evaluate(assignment.Expression)));
        }

        if (set.ElementCount == 0)
        {
            throw new NotSupportedException("The UpdateMany update assigns no fields.");
        }

        return new BsonDocumentUpdateDefinition<TEntity>(new BsonDocument("$set", set));
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
