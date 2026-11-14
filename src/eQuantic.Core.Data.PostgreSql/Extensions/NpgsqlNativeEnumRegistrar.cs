using eQuantic.Core.Data.Relational;
using Npgsql;

namespace eQuantic.Core.Data.PostgreSql.Extensions;

/// <summary>Maps a native enum on the data source: its PostgreSQL type name and, member by member, its labels.</summary>
internal sealed class NpgsqlNativeEnumRegistrar(NpgsqlDataSourceBuilder builder, RelationalNativeEnum declaration)
    : INativeEnumRegistrar
{
    /// <inheritdoc />
    public void Map<TEnum>(string storedName) where TEnum : struct, Enum =>
        builder.MapEnum<TEnum>(storedName, new LabelTranslator(declaration));

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Reached only for an enum declared by [EnumType] on its type; Enum<TEnum>() takes the generic path.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "The enum's fields are read for its labels, and an enum's public fields are its members.")]
    public void Map(Type enumType, string storedName) =>
        builder.MapEnum(enumType, storedName, new LabelTranslator(declaration));

    /// <summary>Names each member by the label the declaration gives it.</summary>
    private sealed class LabelTranslator(RelationalNativeEnum declaration) : INpgsqlNameTranslator
    {
        public string TranslateTypeName(string clrName) => declaration.StoredName;

        public string TranslateMemberName(string clrName) =>
            declaration.Labels.TryGetValue(clrName, out var label) ? label : clrName;
    }
}
