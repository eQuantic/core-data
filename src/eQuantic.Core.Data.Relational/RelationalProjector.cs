using System.Reflection;

namespace eQuantic.Core.Data.Relational;

/// <summary>
///     Builds result instances for projected reads (grouped, union) from ordinal value arrays — positionally
///     through the single constructor (anonymous types) or by member init (named POCOs).
/// </summary>
/// <typeparam name="TResult">The projected result type.</typeparam>
internal sealed class RelationalProjector<TResult>
{
    private readonly ConstructorInfo? _constructor;
    private readonly PropertyInfo[]? _properties;
    private readonly Type[] _targets;

    public RelationalProjector(IReadOnlyList<string> targets, bool constructorProjection)
    {
        if (constructorProjection)
        {
            _constructor = typeof(TResult).GetConstructors().Single();
            _targets = _constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        }
        else
        {
            _properties = targets
                .Select(target => typeof(TResult).GetProperty(target)
                                  ?? throw new NotSupportedException($"'{typeof(TResult).Name}' has no member '{target}'."))
                .ToArray();
            _targets = _properties.Select(property => property.PropertyType).ToArray();
        }
    }

    /// <summary>The CLR type the value at <paramref name="index" /> must convert into.</summary>
    public Type TargetType(int index) => _targets[index];

    /// <summary>Builds one result from the values, in binding order.</summary>
    public TResult Create(object?[] values)
    {
        if (_constructor is not null)
        {
            return (TResult)_constructor.Invoke(values);
        }

        var result = Activator.CreateInstance<TResult>()!;
        for (var index = 0; index < values.Length; index++)
        {
            _properties![index].SetValue(result, values[index]);
        }

        return result;
    }
}
