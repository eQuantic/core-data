using System.Collections;
using System.Linq.Expressions;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.Relational.Repository;

/// <summary>
///     A native relational entity set. Entity inserts are staged on the <see cref="RelationalUnitOfWork" /> and
///     applied on commit; set-based writes run immediately as single SQL statements with real affected-row
///     counts, scoped by the global query filters. Enumerating the set is a full-table read — prefer the
///     repository with a <c>QueryOptions</c>.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class RelationalSet<TEntity> : Data.Repository.ISet<TEntity> where TEntity : class, IEntity
{
    private readonly RelationalUnitOfWork _unitOfWork;
    private readonly RelationalEntityConfiguration _configuration;
    private readonly SqlDialect _dialect;

    internal RelationalSet(RelationalUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
        _configuration = unitOfWork.Configuration<TEntity>();
        _dialect = unitOfWork.Dialect;
    }

    // ---------------------------------------------------------------- IQueryable (no LINQ provider — query through the repository)
    public Type ElementType => typeof(TEntity);

    public Expression Expression =>
        throw new NotSupportedException("The native relational set has no LINQ provider; query through the repository with a QueryOptions.");

    public IQueryProvider Provider =>
        throw new NotSupportedException("The native relational set has no LINQ provider; query through the repository with a QueryOptions.");

    public IEnumerator<TEntity> GetEnumerator() => Execute().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ---------------------------------------------------------------- reads
    public IEnumerable<TEntity> Execute() => ExecuteAsync().GetAwaiter().GetResult();

    private async Task<List<TEntity>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var columns = _configuration.Columns;
        var sql = $"SELECT {string.Join(", ", columns.Select(column => _dialect.Quote(column.Name)))} FROM {_dialect.Quote(_configuration.TableName)}";
        await using var command = await _unitOfWork.CommandAsync(sql, [], cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var entities = new List<TEntity>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entities.Add(RelationalMaterializer.Materialize<TEntity>(reader, columns));
        }

        return entities;
    }

    public TEntity? Find<TKey>(TKey key) => FindAsync(key).GetAwaiter().GetResult();

    public async Task<TEntity?> FindAsync<TKey>(TKey key, CancellationToken cancellationToken = default)
    {
        var columns = _configuration.Columns;
        var sql = $"SELECT {string.Join(", ", columns.Select(column => _dialect.Quote(column.Name)))}"
                  + $" FROM {_dialect.Quote(_configuration.TableName)} WHERE {_dialect.Quote(_configuration.Key.Name)} = @p0";
        await using var command = await _unitOfWork.CommandAsync(sql, [key], cancellationToken).ConfigureAwait(false);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? RelationalMaterializer.Materialize<TEntity>(reader, columns)
            : null;
    }

    // ---------------------------------------------------------------- entity writes (staged → commit)
    public void Insert(TEntity item) => _unitOfWork.StageInsert(item);

    public Task InsertAsync(TEntity item, CancellationToken cancellationToken = default)
    {
        _unitOfWork.StageInsert(item);
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- set-based writes (immediate)
    public long DeleteMany(Expression<Func<TEntity, bool>> filter) => DeleteManyAsync(filter).GetAwaiter().GetResult();

    public async Task<long> DeleteManyAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default)
    {
        // The global filter scopes set-based writes too (a tenant-scoped delete stays tenant-scoped).
        var (where, parameters) = RelationalSql.Where(_dialect, _configuration, filter, _unitOfWork.GlobalFilter<TEntity>());
        var sql = $"DELETE FROM {_dialect.Quote(_configuration.TableName)} WHERE {where}";
        await using var command = await _unitOfWork.CommandAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateExpression) =>
        UpdateManyAsync(filter, updateExpression).GetAwaiter().GetResult();

    public async Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TEntity>> updateExpression, CancellationToken cancellationToken = default)
    {
        var parameters = new List<object?>();
        var set = SqlUpdateRenderer.Render(_dialect, _configuration, updateExpression, parameters);
        var (where, whereParameters) = RelationalSql.Where(_dialect, _configuration, filter, _unitOfWork.GlobalFilter<TEntity>());

        // The WHERE was rendered with its own @p0-based numbering; rebase it after the SET parameters.
        var offset = parameters.Count;
        for (var index = whereParameters.Count - 1; index >= 0; index--)
        {
            where = where.Replace("@p" + index, "@q" + (index + offset));
        }

        where = where.Replace("@q", "@p");
        parameters.AddRange(whereParameters);

        var sql = $"UPDATE {_dialect.Quote(_configuration.TableName)} SET {set} WHERE {where}";
        await using var command = await _unitOfWork.CommandAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
