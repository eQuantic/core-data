using eQuantic.Core.Data.Modeling;

namespace eQuantic.Core.Data.Relational;

/// <summary>
///     An enum stored as its labels (<see cref="EnumTypeAttribute" />, or <c>Enum&lt;TEnum&gt;(...)</c> on the model
///     builder): its type name where the store has enum types, and the label each member is stored as. Each
///     dialect decides the column: a PostgreSQL enum type, a MySQL <c>ENUM(...)</c>, or text restricted to the
///     labels by a <c>CHECK</c>.
/// </summary>
public sealed class RelationalNativeEnum
{
    private readonly Action<INativeEnumRegistrar> _register;

    private RelationalNativeEnum(Type enumType, string storedName, Action<INativeEnumRegistrar> register)
    {
        if (string.IsNullOrWhiteSpace(storedName))
        {
            throw new ArgumentException($"The enum '{enumType.Name}' needs a stored type name.", nameof(storedName));
        }

        EnumType = enumType;
        StoredName = storedName;
        Labels = EnumLabels.Of(enumType);
        _register = register;
    }

    /// <summary>The CLR enum.</summary>
    public Type EnumType { get; }

    /// <summary>The store's type name, unquoted (the dialect quotes it).</summary>
    public string StoredName { get; }

    /// <summary>Each CLR member name and the label the store holds for it, in declaration order.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; }

    /// <summary>Hands this enum to a driver's type mapper (a store with enum types maps it there).</summary>
    /// <param name="registrar">The driver's registrar.</param>
    public void Register(INativeEnumRegistrar registrar) => _register(registrar);

    /// <summary>Describes <typeparamref name="TEnum" />, with its type argument known at compile time (AOT-safe).</summary>
    /// <typeparam name="TEnum">The enum.</typeparam>
    /// <param name="storedName">The store's type name.</param>
    public static RelationalNativeEnum Describe<TEnum>(string storedName) where TEnum : struct, Enum =>
        new(typeof(TEnum), storedName, registrar => registrar.Map<TEnum>(storedName));

    /// <summary>Describes an enum known only at run time (one declared by <see cref="EnumTypeAttribute" />).</summary>
    /// <param name="enumType">The enum.</param>
    /// <param name="storedName">The store's type name.</param>
    public static RelationalNativeEnum Describe(Type enumType, string storedName) =>
        new(enumType, storedName, registrar => registrar.Map(enumType, storedName));

    /// <summary>The enum a column stores, alone or as the element of a collection; <c>null</c> for any other.</summary>
    internal static Type? EnumOf(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying.IsEnum)
        {
            return underlying;
        }

        if (underlying == typeof(string))
        {
            return null;
        }

        var element = underlying.IsArray
            ? underlying.GetElementType()
            : underlying.GetInterfaces().Append(underlying)
                .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                ?.GetGenericArguments()[0];
        element = element is null ? null : Nullable.GetUnderlyingType(element) ?? element;
        return element is { IsEnum: true } ? element : null;
    }
}

/// <summary>A driver's type mapper, handed each enum stored as its labels.</summary>
public interface INativeEnumRegistrar
{
    /// <summary>Maps <typeparamref name="TEnum" /> onto the store's enum type <paramref name="storedName" />.</summary>
    /// <typeparam name="TEnum">The enum.</typeparam>
    /// <param name="storedName">The store's type name, unquoted.</param>
    void Map<TEnum>(string storedName) where TEnum : struct, Enum;

    /// <summary>Maps <paramref name="enumType" /> onto the store's enum type <paramref name="storedName" />.</summary>
    /// <param name="enumType">The enum.</param>
    /// <param name="storedName">The store's type name, unquoted.</param>
    void Map(Type enumType, string storedName);
}
