using System.Data.Common;
using System.Text;
using eQuantic.Core.Data.Query;

namespace eQuantic.Core.Data.Relational;

/// <summary>
///     A database's SQL flavour: identifier quoting, naming conventions, paging syntax, type names for DDL, and
///     the rendering of the constructs that differ between engines (collection membership, tuple comparisons,
///     generated-key retrieval). The shared relational engine renders everything else; a provider package is this
///     class plus a driver.
/// </summary>
public abstract class SqlDialect
{
    /// <summary>Initializes the dialect with the standard function translations.</summary>
    protected SqlDialect() =>
        Functions
            .Map("ToLower", (column, _) => $"LOWER({column})")
            .Map("ToUpper", (column, _) => $"UPPER({column})")
            .Map("Trim", (column, _) => $"TRIM({column})")
            .Map("Like", (column, arguments) => $"{column} LIKE {arguments[0]}")
            .Map("IsNullOrEmpty", (column, _) => $"({column} IS NULL OR {column} = '')")
            .Map("Year", (column, _) => $"EXTRACT(YEAR FROM {column})")
            .Map("Month", (column, _) => $"EXTRACT(MONTH FROM {column})")
            .Map("Day", (column, _) => $"EXTRACT(DAY FROM {column})");

    /// <summary>The OpenTelemetry <c>db.system</c> value (e.g. <c>postgresql</c>, <c>mysql</c>, <c>mssql</c>).</summary>
    public abstract string System { get; }

    /// <summary>The function translations this dialect knows — extend it with <see cref="SqlFunctionRegistry.Map" />.</summary>
    public SqlFunctionRegistry Functions { get; } = new();

    /// <summary>Quotes an identifier (table or column name).</summary>
    public abstract string Quote(string identifier);

    /// <summary>Applies the naming convention to a member name to produce its column name (snake_case by default).</summary>
    public virtual string ColumnName(string memberName) => SnakeCase(memberName);

    /// <summary>Applies the naming convention to an entity name to produce its table name (snake_case by default).</summary>
    public virtual string TableName(string entityName) => SnakeCase(entityName);

    /// <summary>The SQL fragment limiting a query to the bound row count after the bound offset (appended after ORDER BY).</summary>
    public virtual string LimitClause(string limitParameter, string? offsetParameter) =>
        offsetParameter is null ? $"LIMIT {limitParameter}" : $"LIMIT {limitParameter} OFFSET {offsetParameter}";

    /// <summary>Whether a limited query must carry an ORDER BY (SQL Server's <c>OFFSET/FETCH</c>); the key is used when unsorted.</summary>
    public virtual bool RequiresOrderByForLimit => false;

    /// <summary>
    ///     One <c>ORDER BY</c> term. With a placement of nulls, the base dialect orders on whether the column is null
    ///     first (<c>CASE WHEN col IS NULL THEN 0 ELSE 1 END, col ASC</c>), which every engine understands; a dialect
    ///     with its own syntax (<c>NULLS FIRST</c>) overrides it.
    /// </summary>
    /// <param name="column">The quoted column (or a qualified one).</param>
    /// <param name="descending">Whether the sort is descending.</param>
    /// <param name="nulls">Where the rows whose column is null go.</param>
    public virtual string OrderTerm(string column, bool descending, eQuantic.Core.Data.Repository.Options.NullsOrder nulls)
    {
        var term = $"{column} {(descending ? "DESC" : "ASC")}";
        return nulls == eQuantic.Core.Data.Repository.Options.NullsOrder.Default
            ? term
            : $"CASE WHEN {column} IS NULL THEN {(nulls == eQuantic.Core.Data.Repository.Options.NullsOrder.First ? 0 : 1)} " +
              $"ELSE {(nulls == eQuantic.Core.Data.Repository.Options.NullsOrder.First ? 1 : 0)} END, {term}";
    }

    /// <summary>The literal for an always-false predicate (an empty <c>IN</c>); some dialects have no <c>FALSE</c>.</summary>
    public virtual string FalseLiteral => "FALSE";

    /// <summary>The DDL type for a CLR type (used by <c>CREATE TABLE</c> migrations).</summary>
    public abstract string SqlType(Type type);

    /// <summary>The DDL type for a column, honouring its declared facets (text length, decimal precision/scale).</summary>
    /// <param name="column">The column.</param>
    public string SqlType(RelationalColumn column) =>
        SizedSqlType(column.StoredType, column.Length, column.Precision, column.Scale);

    /// <summary>
    ///     The DDL type for a type with facets: <c>varchar(n)</c> for sized text, <c>numeric(p,s)</c> for sized
    ///     decimals, the plain <see cref="SqlType(Type)" /> otherwise. Dialects override the spellings.
    /// </summary>
    /// <param name="type">The stored CLR type.</param>
    /// <param name="length">The maximum text length (0 = default).</param>
    /// <param name="precision">The decimal precision (0 = default).</param>
    /// <param name="scale">The decimal scale.</param>
    public virtual string SizedSqlType(Type type, int length, int precision, int scale)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(string) && length > 0)
        {
            return SizedTextType(length);
        }

        if (underlying == typeof(decimal) && precision > 0)
        {
            return $"numeric({precision}, {scale})";
        }

        return SqlType(type);
    }

    /// <summary>The sized text type (<c>varchar(n)</c> by default).</summary>
    /// <param name="length">The maximum length.</param>
    protected virtual string SizedTextType(int length) => $"varchar({length})";

    /// <summary>
    ///     Reduces a type to one spelling, so the type this dialect would emit and the one the catalogue reports
    ///     can be compared without every synonym reading as a difference.
    ///     <para>
    ///         A drift check is only worth running if a clean database is silent. Every store names some types
    ///         more than one way — <c>character varying</c> and <c>varchar</c>, <c>int</c> and <c>integer</c> —
    ///         and comparing the raw strings would report all of them, which teaches people to ignore the report.
    ///         Dialects override this with their own synonyms; the base only strips case and spacing.
    ///     </para>
    /// </summary>
    /// <param name="storedType">A type as written in DDL or reported by the catalogue.</param>
    public virtual string NormalizeStoredType(string storedType)
    {
        var text = storedType.Trim().ToLowerInvariant();
        var open = text.IndexOf('(');
        if (open < 0)
        {
            return Synonym(text);
        }

        var close = text.IndexOf(')', open);
        if (close < 0)
        {
            return Synonym(text);
        }

        // The facet travels separately so the base name can be looked up: numeric(18, 2) -> numeric(18,2).
        var facet = string.Concat(text[(open + 1)..close].Where(character => !char.IsWhiteSpace(character)));
        return $"{Synonym(text[..open].TrimEnd())}({facet}){text[(close + 1)..].Trim()}";
    }

    /// <summary>The one spelling this dialect uses for a type named without its facets.</summary>
    /// <param name="baseType">The lower-cased type name.</param>
    protected virtual string Synonym(string baseType) => baseType;

    /// <summary>
    ///     A query listing every column of the schema in use, as four values in order: the table's name, the
    ///     column's name, its type <b>spelled the way this dialect spells it in DDL</b>, and whether it accepts
    ///     null. <c>null</c> when the dialect cannot read its own catalogue, which makes drift unanswerable
    ///     rather than answered wrongly.
    ///     <para>
    ///         Each dialect writes its own, and composes the type in SQL rather than leaving it to be reassembled
    ///         from parts afterwards. Catalogues disagree about where a type's facets live — one keeps the whole
    ///         spelling in a column, another splits length from precision from fractional seconds — and the
    ///         disagreement is far better settled by the store that knows the answer.
    ///     </para>
    /// </summary>
    public virtual string? IntrospectColumnsSql => null;

    /// <summary>The DDL column suffix declaring a database-generated key (e.g. <c>GENERATED BY DEFAULT AS IDENTITY</c>).</summary>
    public abstract string GeneratedKeyDdl { get; }

    /// <summary>
    ///     Builds an INSERT, reading the generated key back when <paramref name="returningKey" /> is supplied
    ///     (<c>RETURNING</c> / <c>OUTPUT INSERTED</c>). The base dialect cannot read keys back — declare a
    ///     client-generated key, or use a dialect that can.
    /// </summary>
    /// <param name="quotedTable">The quoted table.</param>
    /// <param name="columns">The quoted column list.</param>
    /// <param name="values">The bound value list.</param>
    /// <param name="returningKey">The quoted generated-key column to read back, or <c>null</c>.</param>
    public virtual string InsertSql(string quotedTable, string columns, string values, string? returningKey) =>
        returningKey is null
            ? $"INSERT INTO {quotedTable} ({columns}) VALUES ({values})"
            : throw new NotSupportedException(
                $"{GetType().Name} cannot read a generated key back from an insert; declare a client-generated key instead.");

    /// <summary>Builds an idempotent CREATE TABLE statement.</summary>
    /// <param name="quotedTable">The quoted table.</param>
    /// <param name="columnsDdl">The column declaration list.</param>
    public virtual string CreateTableSql(string quotedTable, string columnsDdl) =>
        $"CREATE TABLE IF NOT EXISTS {quotedTable} ({columnsDdl})";

    /// <summary>Builds a CREATE INDEX statement (idempotent where the dialect supports it; migration history guards re-runs otherwise).</summary>
    /// <param name="quotedName">The quoted index name.</param>
    /// <param name="quotedTable">The quoted table.</param>
    /// <param name="columns">The quoted key column list (with directions).</param>
    /// <param name="unique">Whether the index enforces uniqueness.</param>
    public virtual string CreateIndexSql(string quotedName, string quotedTable, string columns, bool unique) =>
        CreateIndexSql(quotedName, quotedTable, columns, unique, eQuantic.Core.Data.Migration.IndexMethod.Default, filter: null);

    /// <summary>
    ///     The DDL creating an index with a structure and an optional filtered predicate (already rendered as a
    ///     SQL fragment with inlined literals). The base dialect builds default-structure indexes, filtered or
    ///     not; a method it has no structure for is rejected with guidance.
    /// </summary>
    /// <param name="quotedName">The quoted index name.</param>
    /// <param name="quotedTable">The quoted table.</param>
    /// <param name="columns">The rendered key column list.</param>
    /// <param name="unique">Whether the index enforces uniqueness.</param>
    /// <param name="method">The index structure.</param>
    /// <param name="filter">The rendered partial-index predicate, or <c>null</c>.</param>
    public virtual string CreateIndexSql(string quotedName, string quotedTable, string columns, bool unique,
        eQuantic.Core.Data.Migration.IndexMethod method, string? filter)
    {
        if (method != eQuantic.Core.Data.Migration.IndexMethod.Default)
        {
            throw new NotSupportedException(
                $"{GetType().Name} has no '{method}' index structure; use a default index, or the store's native tooling via Run(...).");
        }

        return $"CREATE {(unique ? "UNIQUE " : string.Empty)}INDEX IF NOT EXISTS {quotedName} ON {quotedTable} ({columns})"
               + (filter is not null ? $" WHERE {filter}" : string.Empty);
    }

    /// <summary>
    ///     The DDL materializing a model-declared search index (<c>SearchIndex(...)</c> / <c>[SearchIndex]</c>)
    ///     on a text column, in execution order — empty when the dialect has no equivalent structure. The
    ///     declaration never changes semantics (<c>LIKE</c> pushes down regardless); it only changes the plan,
    ///     which is why a dialect without the structure ignores it instead of refusing.
    /// </summary>
    /// <param name="indexName">The unquoted index name (the dialect quotes it).</param>
    /// <param name="quotedTable">The quoted table.</param>
    /// <param name="quotedColumn">The quoted column.</param>
    public virtual IReadOnlyList<string> SearchIndexSql(string indexName, string quotedTable, string quotedColumn) => [];

    /// <summary>
    ///     Whether the engine has a <b>native bulk-load path</b> for this dialect (PostgreSQL <c>COPY</c>,
    ///     SQL Server <c>SqlBulkCopy</c>, MySQL's bulk loader). <c>BulkInsertAsync</c> refuses on dialects that
    ///     answer <c>false</c> rather than quietly running an ordinary batch — asking for a bulk load and
    ///     getting row-by-row inserts is exactly the kind of silent cost this engine does not ship.
    /// </summary>
    public virtual bool SupportsBulkInsert => false;

    /// <summary>
    ///     Bulk-loads rows through the store's native mechanism. Implemented by the dialects that declare
    ///     <see cref="SupportsBulkInsert" />; each row carries the column values in <paramref name="columns" />
    ///     order, already converted to their stored form by the engine.
    /// </summary>
    /// <param name="connection">The open connection (inside the caller's transaction, when there is one).</param>
    /// <param name="transaction">The ambient transaction, or <c>null</c>.</param>
    /// <param name="quotedTable">The quoted target table.</param>
    /// <param name="columns">The target columns, in the order each row's values arrive.</param>
    /// <param name="rows">The rows to load.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of rows loaded.</returns>
    public virtual Task<long> BulkInsertAsync(DbConnection connection, DbTransaction? transaction, string quotedTable,
        IReadOnlyList<RelationalColumn> columns, IReadOnlyList<object?[]> rows, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            $"{GetType().Name} has no native bulk-load path; stage the entities and Commit() — the flush already " +
            "batches them into one round trip.");

    /// <summary>
    ///     How this engine takes a named lock (<c>ILockingUnitOfWork.AcquireLockAsync</c>), or <c>null</c> when it has
    ///     no lock of its own: the unit of work then refuses, rather than imitate one with a table.
    /// </summary>
    public virtual SqlNamedLocks? NamedLocks => null;

    /// <summary>The DDL adding a column to an existing table (added nullable, matching create-table semantics).</summary>
    /// <param name="quotedTable">The quoted table.</param>
    /// <param name="quotedColumn">The quoted column.</param>
    /// <param name="sqlType">The column's SQL type.</param>
    public virtual string AddColumnSql(string quotedTable, string quotedColumn, string sqlType) =>
        $"ALTER TABLE {quotedTable} ADD {quotedColumn} {sqlType}";

    /// <summary>The DDL dropping a column from an existing table.</summary>
    /// <param name="quotedTable">The quoted table.</param>
    /// <param name="quotedColumn">The quoted column.</param>
    public virtual string DropColumnSql(string quotedTable, string quotedColumn) =>
        $"ALTER TABLE {quotedTable} DROP COLUMN {quotedColumn}";

    /// <summary>
    ///     The DDL renaming a table. The target arrives unquoted because not every dialect renames with a
    ///     statement — SQL Server calls a procedure, and its second argument is a bare name.
    /// </summary>
    /// <param name="quotedTable">The quoted table, as it is now.</param>
    /// <param name="target">The name it takes, unquoted.</param>
    public virtual string RenameTableSql(string quotedTable, string target) =>
        $"ALTER TABLE {quotedTable} RENAME TO {Quote(target)}";

    /// <summary>The DDL dropping a table, and everything that depended on it.</summary>
    /// <param name="quotedTable">The quoted table.</param>
    public virtual string DropTableSql(string quotedTable) =>
        $"DROP TABLE {quotedTable}";

    /// <summary>
    ///     Renders a value as an inline SQL literal — DDL (a filtered index's predicate) cannot carry bind
    ///     parameters. Values a dialect cannot inline are rejected with guidance.
    /// </summary>
    /// <param name="value">The value (already normalized by <see cref="BindValue" />).</param>
    public virtual string Literal(object? value) => value switch
    {
        null => "NULL",
        string text => "'" + text.Replace("'", "''") + "'",
        bool flag => flag ? "TRUE" : "FALSE",
        Guid guid => $"'{guid}'",
        DateTime dateTime => $"'{dateTime:yyyy-MM-dd HH:mm:ss.fffffff}'",
        global::System.IFormattable number => number.ToString(null, global::System.Globalization.CultureInfo.InvariantCulture)!,
        _ => throw new NotSupportedException(
            $"Cannot inline a '{value.GetType().Name}' literal into DDL; simplify the filtered-index predicate."),
    };

    /// <summary>
    ///     Renders a collection-membership test (<c>member CONTAINS value</c> / <c>CONTAINS KEY</c>) for stores
    ///     with native collection columns; the base dialect cannot express it (the clause degrades to residual).
    /// </summary>
    /// <param name="column">The quoted column.</param>
    /// <param name="parameter">The bound value's parameter marker.</param>
    /// <param name="key">Whether the test targets a map key.</param>
    public virtual string CollectionContains(string column, string parameter, bool key) =>
        throw new NotSupportedException($"{GetType().Name} has no native collection membership; the clause runs client-side.");

    /// <summary>Renders a row-wise tuple comparison; the base dialect cannot express it.</summary>
    /// <param name="columns">The quoted columns.</param>
    /// <param name="op">The comparison operator.</param>
    /// <param name="parameters">The bound values' parameter markers.</param>
    public virtual string TupleComparison(IReadOnlyList<string> columns, ComparisonOperator op, IReadOnlyList<string> parameters) =>
        throw new NotSupportedException($"{GetType().Name} has no native tuple comparison; the clause runs client-side.");

    /// <summary>
    ///     Whether a member of this CLR type maps as a <b>document column</b>. Every dialect stores a JSON member
    ///     (<c>JsonDocument</c>, <c>JsonElement</c>, <c>JsonNode</c>) in its own JSON form; a dialect may add more
    ///     (PostgreSQL: scalar-keyed dictionaries as <c>jsonb</c>).
    /// </summary>
    /// <param name="type">The member's CLR type.</param>
    public virtual bool IsDocumentColumn(Type type) => Modeling.JsonMembers.Is(type);

    private readonly Dictionary<Type, RelationalNativeEnum> _nativeEnums = [];

    /// <summary>
    ///     Declares an enum stored as its labels (<see cref="Modeling.EnumTypeAttribute" /> or
    ///     <c>Enum&lt;TEnum&gt;(...)</c>): each dialect gives it its own column (<see cref="EnumSqlType" />) and
    ///     binds its values as labels (<see cref="BindValue" />).
    /// </summary>
    /// <param name="declaration">The enum, its stored type name and labels.</param>
    public void DeclareNativeEnum(RelationalNativeEnum declaration)
    {
        if (_nativeEnums.TryGetValue(declaration.EnumType, out var declared)
            && !string.Equals(declared.StoredName, declaration.StoredName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{declaration.EnumType.Name}' is already declared stored as labels under \"{declared.StoredName}\", " +
                $"and cannot also be \"{declaration.StoredName}\".");
        }

        _nativeEnums[declaration.EnumType] = declaration;
    }

    /// <summary>The enums declared stored as their labels.</summary>
    public IReadOnlyCollection<RelationalNativeEnum> NativeEnums => _nativeEnums.Values;

    /// <summary>The declaration of <paramref name="type" /> (nullable unwrapped) when it is stored as its labels.</summary>
    /// <param name="type">A CLR type.</param>
    public RelationalNativeEnum? NativeEnumOf(Type type) =>
        _nativeEnums.GetValueOrDefault(Nullable.GetUnderlyingType(type) ?? type);

    /// <summary>
    ///     The column type of an enum stored as its labels. Text wide enough for its longest label by default,
    ///     restricted to the labels by <see cref="CheckConstraintSql" />; a dialect with enum types overrides it.
    /// </summary>
    /// <param name="declaration">The enum.</param>
    public virtual string EnumSqlType(RelationalNativeEnum declaration) =>
        SizedTextType(Math.Max(1, declaration.Labels.Values.Max(label => label.Length)));

    /// <summary>Whether <see cref="EnumSqlType" /> already restricts the column to the labels (an enum type does).</summary>
    public virtual bool EnumTypeRestrictsLabels => false;

    /// <summary>
    ///     The column-level <c>CHECK</c> a column carries, or <c>null</c>: by default, an enum stored as text is held
    ///     to its labels. A dialect adds its own (SQL Server: a JSON member's text must be JSON).
    /// </summary>
    /// <param name="quotedColumn">The quoted column.</param>
    /// <param name="column">The column.</param>
    public virtual string? CheckConstraintSql(string quotedColumn, RelationalColumn column) =>
        !EnumTypeRestrictsLabels && NativeEnumOf(column.StoredType) is { } native
            ? $"CHECK ({quotedColumn} IN ({string.Join(", ", native.Labels.Values.Select(TextLiteral))}))"
            : null;

    /// <summary>A text literal (quotes doubled), for DDL that cannot take parameters.</summary>
    /// <param name="text">The text.</param>
    public virtual string TextLiteral(string text) => "'" + text.Replace("'", "''") + "'";

    /// <summary>
    ///     The DDL creating an enum type when it does not exist yet, or <c>null</c> when the dialect has none. A
    ///     type that exists is left as it is: a migration adds nothing to it and removes nothing from it.
    /// </summary>
    /// <param name="declaration">The enum.</param>
    public virtual string? CreateEnumTypeSql(RelationalNativeEnum declaration) => null;

    /// <summary>
    ///     Makes the driver read the store's types again after a migration created some (an enum type the
    ///     data source looked for before it existed). Nothing to do by default.
    /// </summary>
    /// <param name="connection">The migration's open connection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public virtual Task ReloadTypesAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    ///     How the store fills a store-generated column, as the DDL after its type: <c>NOT NULL</c> (the store always
    ///     gives it a value) and then the declared default, the dialect's sequence, or the dialect's clock.
    /// </summary>
    /// <param name="table">The table's stored name, unquoted.</param>
    /// <param name="column">The column.</param>
    public string StoreGeneratedDdl(string table, RelationalColumn column) =>
        " NOT NULL " + (column.StoreDefaultSql is { } defaultSql
            ? "DEFAULT " + defaultSql
            : column.StoreValue == Modeling.StoreValue.Sequence
                ? SequenceDdl(table, column)
                : "DEFAULT " + ClockSql(column.StoredType));

    /// <summary>The DDL after <c>NOT NULL</c> that numbers a store-generated integral column (an identity by default).</summary>
    /// <param name="table">The table's stored name, unquoted.</param>
    /// <param name="column">The column.</param>
    public virtual string SequenceDdl(string table, RelationalColumn column) => GeneratedKeyDdl;

    /// <summary>What has to exist before a table with this store-generated column is created (a sequence object).</summary>
    /// <param name="table">The table's stored name, unquoted.</param>
    /// <param name="column">The column.</param>
    public virtual IReadOnlyList<string> StoreGeneratedPrerequisiteSql(string table, RelationalColumn column) => [];

    /// <summary>The expression for the store's clock, in UTC, as a default for a column of <paramref name="type" />.</summary>
    /// <param name="type">The column's CLR type (<c>DateTime</c> or <c>DateTimeOffset</c>).</param>
    public abstract string ClockSql(Type type);

    /// <summary>
    ///     Whether an insert can hand values the store assigned back to the caller (<c>RETURNING</c>,
    ///     <c>OUTPUT INSERTED</c>, or a read of the row by its key in the same batch): what a database-generated
    ///     key and a store-generated column need.
    /// </summary>
    public virtual bool ReadsBackInsertedValues => false;

    /// <summary>
    ///     Whether the dialect reads inserted values back by reading the row by its key after the insert (no
    ///     <c>RETURNING</c>): the insert is then handed its key predicate. Such a dialect cannot read back a
    ///     generated key.
    /// </summary>
    public virtual bool ReadsBackByKey => false;

    /// <summary>
    ///     Builds an INSERT that reads back every column in <paramref name="returning" />, in order: a generated
    ///     key first, then the store-generated columns. With none it is a plain INSERT, and with one it is
    ///     <see cref="InsertSql(string, string, string, string?)" />; a dialect that reads back several overrides it.
    /// </summary>
    /// <param name="quotedTable">The quoted table.</param>
    /// <param name="columns">The quoted column list.</param>
    /// <param name="values">The bound value list.</param>
    /// <param name="returning">The quoted columns to read back.</param>
    /// <param name="keyPredicate">The row's key predicate, for a dialect that <see cref="ReadsBackByKey" />.</param>
    public virtual string InsertSql(string quotedTable, string columns, string values, IReadOnlyList<string> returning,
        string? keyPredicate = null) =>
        returning.Count switch
        {
            0 => InsertSql(quotedTable, columns, values, (string?)null),
            1 => InsertSql(quotedTable, columns, values, returning[0]),
            _ => throw new NotSupportedException(
                $"{GetType().Name} cannot read several values back from an insert; declare client-assigned values instead."),
        };

    /// <summary>
    ///     The CLR type a bulk loader declares for a column: the stored type, except where a value is bound in
    ///     another form (an enum stored as its labels and a JSON member are bound as text).
    /// </summary>
    /// <param name="column">The column.</param>
    public virtual Type BoundType(RelationalColumn column)
    {
        var stored = Nullable.GetUnderlyingType(column.StoredType) ?? column.StoredType;
        return NativeEnumOf(stored) is not null || Modeling.JsonMembers.Is(stored) ? typeof(string) : stored;
    }

    /// <summary>
    ///     Configures a just-created parameter for a bound value — the hook a dialect uses to type values its
    ///     driver cannot infer (e.g. a dictionary into a <c>jsonb</c> parameter). The base dialect does nothing.
    /// </summary>
    /// <param name="parameter">The parameter (its name and value are already set).</param>
    /// <param name="value">The bound value.</param>
    public virtual void ConfigureParameter(System.Data.Common.DbParameter parameter, object? value)
    {
    }

    /// <summary>Renders a collection-mutating SET fragment (<c>col = col + items</c>), for stores with collection columns.</summary>
    /// <param name="column">The quoted column.</param>
    /// <param name="parameter">The bound items' parameter marker.</param>
    /// <param name="remove">Whether the items are removed instead of added.</param>
    /// <param name="prepend">Whether added items go at the front.</param>
    public virtual string CollectionMutation(string column, string parameter, bool remove, bool prepend) =>
        throw new NotSupportedException($"{GetType().Name} has no native collection columns; load the rows and Modify them instead.");

    /// <summary>The DDL altering a column's type (a dialect may need a cast clause).</summary>
    public virtual string AlterColumnType(string quotedTable, string quotedColumn, string sqlType) =>
        $"ALTER TABLE {quotedTable} ALTER COLUMN {quotedColumn} TYPE {sqlType}";

    /// <summary>
    ///     Converts a value before binding: an enum stored as its labels binds as its label, and a JSON member as its
    ///     JSON text. A dialect adds its own coercions (or passes those through to a driver that maps them).
    /// </summary>
    public virtual object? BindValue(object? value) => value switch
    {
        Enum enumValue when NativeEnumOf(enumValue.GetType()) is not null => Modeling.EnumLabels.Label(enumValue),
        _ when Modeling.JsonMembers.IsValue(value) => Modeling.JsonMembers.Text(value),
        _ => value,
    };

    private static string SnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (char.IsUpper(character))
            {
                if (index > 0 && (char.IsLower(name[index - 1]) || (index + 1 < name.Length && char.IsLower(name[index + 1]))))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
