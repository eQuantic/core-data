using System.Collections;
using System.Linq.Expressions;
using eQuantic.Core.Data.Repository;
using global::Cassandra;

namespace eQuantic.Core.Data.Cassandra.Repository;

/// <summary>
///     A native Apache Cassandra entity set. Entity inserts are staged on the <see cref="CassandraUnitOfWork" />
///     and applied on commit; set-based writes run immediately as CQL. Cassandra <c>DELETE</c>/<c>UPDATE</c> need
///     the primary key in their <c>WHERE</c>, so those reject non-key filters; enumerating the set is a full-table
///     scan — prefer the repository with a key-scoped <c>QueryOptions</c>.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class CassandraSet<TEntity> : Data.Repository.ISet<TEntity> where TEntity : class, IEntity
{
    private readonly CassandraUnitOfWork _unitOfWork;
    private readonly ISession _session;
    private readonly CassandraEntityConfiguration _configuration;

    internal CassandraSet(CassandraUnitOfWork unitOfWork, ISession session)
    {
        _unitOfWork = unitOfWork;
        _session = session;
        _configuration = unitOfWork.Configuration<TEntity>();
    }

    // ---------------------------------------------------------------- IQueryable (Cassandra has no arbitrary LINQ)
    public Type ElementType => typeof(TEntity);

    public Expression Expression =>
        throw new NotSupportedException("Cassandra does not support arbitrary LINQ; query through the repository with a QueryOptions.");

    public IQueryProvider Provider =>
        throw new NotSupportedException("Cassandra does not support arbitrary LINQ; query through the repository with a QueryOptions.");

    public IEnumerator<TEntity> GetEnumerator() => Execute().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ---------------------------------------------------------------- reads
    public IEnumerable<TEntity> Execute() => ExecuteAsync().GetAwaiter().GetResult();

    private async Task<List<TEntity>> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var rows = await CassandraStatements.ExecuteAsync(_session, $"SELECT * FROM {_configuration.TableName}", []).ConfigureAwait(false);
        return rows.Select(row => CassandraMapper.Materialize<TEntity>(_configuration, row)).ToList();
    }

    public TEntity? Find<TKey>(TKey key) => FindAsync(key).GetAwaiter().GetResult();

    public async Task<TEntity?> FindAsync<TKey>(TKey key, CancellationToken cancellationToken = default)
    {
        var rows = await CassandraStatements.ExecuteAsync(_session,
            $"SELECT * FROM {_configuration.TableName} WHERE {_configuration.KeyColumn} = ? LIMIT 1", [key]).ConfigureAwait(false);
        var row = rows.FirstOrDefault();
        return row is null ? null : CassandraMapper.Materialize<TEntity>(_configuration, row);
    }

    // ---------------------------------------------------------------- entity writes (staged → commit)
    public void Insert(TEntity item) => _unitOfWork.StageUpsert(item);

    public Task InsertAsync(TEntity item, CancellationToken cancellationToken = default)
    {
        _unitOfWork.StageUpsert(item);
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- set-based writes (immediate)
    public long DeleteMany(Expression<Func<TEntity, bool>> filter) => DeleteManyAsync(filter).GetAwaiter().GetResult();

    public async Task<long> DeleteManyAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default)
    {
        // The global filter scopes set-based writes too (a tenant-scoped delete stays tenant-scoped).
        var (where, values, requiresFiltering) = CassandraCql.Where<TEntity>(_configuration, null, filter, _unitOfWork.GlobalFilter<TEntity>());
        if (requiresFiltering)
        {
            throw new NotSupportedException("Cassandra DELETE requires the partition key; it cannot filter on non-key columns.");
        }

        var count = await CountAsync(where, values, cancellationToken).ConfigureAwait(false);
        await CassandraStatements.ExecuteAsync(_session, $"DELETE FROM {_configuration.TableName} WHERE {where}", values).ConfigureAwait(false);
        return count;
    }

    public long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateExpression) =>
        UpdateManyAsync(filter, updateExpression).GetAwaiter().GetResult();

    public async Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TEntity>> updateExpression, CancellationToken cancellationToken = default)
    {
        var (where, whereValues, requiresFiltering) = CassandraCql.Where<TEntity>(_configuration, null, filter, _unitOfWork.GlobalFilter<TEntity>());
        if (requiresFiltering)
        {
            throw new NotSupportedException("Cassandra UPDATE requires the primary key; it cannot filter on non-key columns.");
        }

        var (set, setValues) = CassandraUpdate.Build(_configuration, updateExpression);
        var count = await CountAsync(where, whereValues, cancellationToken).ConfigureAwait(false);
        await CassandraStatements.ExecuteAsync(_session,
            $"UPDATE {_configuration.TableName} SET {set} WHERE {where}", setValues.Concat(whereValues).ToArray()).ConfigureAwait(false);
        return count;
    }

    private async Task<long> CountAsync(string where, object?[] values, CancellationToken cancellationToken)
    {
        var cql = $"SELECT COUNT(*) FROM {_configuration.TableName}" + (where.Length > 0 ? $" WHERE {where}" : string.Empty);
        var rows = await CassandraStatements.ExecuteAsync(_session, cql, values).ConfigureAwait(false);
        return rows.First().GetValue<long>(0);
    }
}
