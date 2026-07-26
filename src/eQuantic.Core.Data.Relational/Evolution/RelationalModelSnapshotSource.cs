using eQuantic.Core.Data.Evolution;

namespace eQuantic.Core.Data.Relational.Evolution;

/// <summary>
///     Describes a <see cref="RelationalModel" /> as a store-neutral snapshot: tables become collections, columns
///     become fields, and the facets that size a column travel with them so a later comparison can see a
///     <c>varchar(50)</c> widen to a <c>varchar(200)</c>.
/// </summary>
public sealed class RelationalModelSnapshotSource : IModelSnapshotSource
{
    private readonly RelationalModel _model;

    /// <summary>Initializes the source over the registered model.</summary>
    /// <param name="model">The relational model.</param>
    /// <param name="dialect">The dialect, whose name identifies the store in the snapshot.</param>
    public RelationalModelSnapshotSource(RelationalModel model, SqlDialect dialect)
    {
        _model = model;
        Provider = dialect.System;
    }

    /// <inheritdoc />
    public string Provider { get; }

    /// <inheritdoc />
    public ModelSnapshot Describe() => new(Provider, _model.Configurations.Values
        .Select(Describe)
        .OrderBy(entity => entity.EntityType, StringComparer.Ordinal)
        .ToList());

    private static EntitySnapshot Describe(RelationalEntityConfiguration configuration) =>
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

    private static FieldSnapshot Describe(RelationalColumn column)
    {
        var stored = column.StoredType;
        var underlying = Nullable.GetUnderlyingType(stored);
        return new FieldSnapshot(column.Property.Name, column.Name, (underlying ?? stored).FullName ?? stored.Name)
        {
            Length = column.Length,
            Precision = column.Precision,
            Scale = column.Scale,
            // A reference type is nullable unless the model says otherwise; the engine has no non-nullable
            // reference concept, so what matters to a comparison is the value-type case being explicit.
            Nullable = underlying is not null || !stored.IsValueType,
            PreviousNames = column.PreviousNames,
            DefaultLiteral = CSharpLiteral.Render(column.DefaultValue),
        };
    }
}
