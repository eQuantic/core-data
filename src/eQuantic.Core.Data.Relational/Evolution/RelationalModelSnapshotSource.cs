using eQuantic.Core.Data.Evolution;

namespace eQuantic.Core.Data.Relational.Evolution;

/// <summary>
///     Describes a <see cref="RelationalModel" /> as a store-neutral snapshot: tables become collections, columns
///     become fields, and the facets that size a column travel with them so a later comparison can see a
///     <c>varchar(50)</c> widen to a <c>varchar(200)</c>.
///     <para>
///         A column of an enum the dialect stores as its labels is recorded as those labels
///         (<see cref="StoredForm.Labels" />), and a store-generated column with what the store assigns it, because
///         neither shows in the CLR type: switching an enum to its labels, or a column to an identity, changes what
///         the table holds while the member's type stays the same.
///     </para>
/// </summary>
public sealed class RelationalModelSnapshotSource : IModelSnapshotSource
{
    private readonly SqlDialect _dialect;
    private readonly RelationalModel _model;

    /// <summary>Initializes the source over the registered model.</summary>
    /// <param name="model">The relational model.</param>
    /// <param name="dialect">The dialect, whose name identifies the store in the snapshot.</param>
    public RelationalModelSnapshotSource(RelationalModel model, SqlDialect dialect)
    {
        _model = model;
        _dialect = dialect;
        Provider = dialect.System;
    }

    /// <inheritdoc />
    public string Provider { get; }

    /// <inheritdoc />
    public ModelSnapshot Describe() => new(Provider, _model.Configurations.Values
        .Select(Describe)
        .OrderBy(entity => entity.EntityType, StringComparer.Ordinal)
        .ToList());

    private EntitySnapshot Describe(RelationalEntityConfiguration configuration) =>
        new(configuration.EntityType.FullName ?? configuration.EntityType.Name,
            configuration.TableName,
            configuration.Columns
                .Select(Describe)
                .OrderBy(field => field.Member, StringComparer.Ordinal)
                .ToList())
        {
            Keys = configuration.Keys.Select(key => key.Property.Name).ToList(),
            KeyIsGenerated = configuration.KeyIsGenerated,
            Clustering = configuration.ClusteringColumns
                .Select(clustering => new ClusteringSnapshot(clustering.Column.Property.Name, clustering.Descending))
                .ToList(),
            ConcurrencyField = configuration.ConcurrencyToken?.Property.Name,
            Search = configuration.SearchColumns
                .Select(search => new SearchSnapshot(search.Column.Property.Name, search.Mode.ToString()))
                .ToList(),
        };

    private FieldSnapshot Describe(RelationalColumn column)
    {
        var stored = column.StoredType;
        var underlying = Nullable.GetUnderlyingType(stored);
        var labels = LabelEnumOf(column);
        return new FieldSnapshot(column.Property.Name, column.Name, StoredTypeOf(column))
        {
            Length = column.Length,
            Precision = column.Precision,
            Scale = column.Scale,
            // A reference type is nullable unless the model says otherwise; the engine has no non-nullable
            // reference concept, so what matters to a comparison is the value-type case being explicit.
            Nullable = underlying is not null || !stored.IsValueType,
            PreviousNames = column.PreviousNames,
            DefaultLiteral = CSharpLiteral.Render(column.DefaultValue),
            Generated = GeneratedOf(column),
            Labels = labels is null ? [] : LabelSnapshot.Of(labels),
        };
    }

    /// <summary>
    ///     The stored CLR type's full name, or the labels of the enum the dialect stores the column as (alone, or the
    ///     elements of an array column). A converted column stores what its converter writes, whatever the member is.
    /// </summary>
    private string StoredTypeOf(RelationalColumn column)
    {
        var stored = Nullable.GetUnderlyingType(column.StoredType) ?? column.StoredType;
        if (LabelEnumOf(column) is { } enumType)
        {
            return stored.IsEnum ? StoredForm.Labels(enumType) : StoredForm.LabelCollection(enumType);
        }

        return stored.FullName ?? stored.Name;
    }

    /// <summary>The enum the dialect stores the column as labels of (alone, or an array's elements), or <c>null</c>.</summary>
    private Type? LabelEnumOf(RelationalColumn column) =>
        column.Converter is null && RelationalNativeEnum.EnumOf(column.StoredType) is { } enumType &&
        _dialect.NativeEnumOf(enumType) is not null
            ? enumType
            : null;

    /// <summary>What the store assigns the column on insert, or <c>null</c> when the application writes it.</summary>
    private static string? GeneratedOf(RelationalColumn column) =>
        !column.IsStoreGenerated ? null
        : column.StoreDefaultSql is { } sql ? StoredForm.GeneratedDefault(sql)
        : StoredForm.Generated(column.StoreValue);
}
