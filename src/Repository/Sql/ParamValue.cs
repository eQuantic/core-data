namespace eQuantic.Core.Data.Repository.Sql;

public sealed class ParamValue
{
    public string Name { get; }
    public object Value { get; }

    private ParamValue(string name, object value) : this(value)
    {
        Name = name;
    }

    private ParamValue(object value)
    {
        Value = value;
    }

    public static ParamValue Create(string name, object value) => new ParamValue(name, value);
    public static ParamValue Create(object value) => new ParamValue(value);

    public override bool Equals(object obj)
    {
        return obj is ParamValue paramValue && (
            !string.IsNullOrEmpty(paramValue.Name)
                ? paramValue.Name == Name
                : paramValue.Name == Name && paramValue.Value.Equals(Value)
        );
    }

    public override int GetHashCode()
    {
        return (Name, Value).GetHashCode();
    }
}
