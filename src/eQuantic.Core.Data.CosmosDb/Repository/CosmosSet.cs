using System.Collections;
using System.Linq.Expressions;
using eQuantic.Core.Data.Repository;
using eQuantic.Linq.Expressions;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;

namespace eQuantic.Core.Data.CosmosDb.Repository;

/// <summary>
///     A native Azure Cosmos DB entity set. Reads project through the SDK's LINQ provider; entity writes are
///     staged on the <see cref="CosmosUnitOfWork" /> and applied on commit; set-based writes
///     (<see cref="DeleteMany" />/<see cref="UpdateMany" />) query the matching items and then delete or patch
///     each one (Cosmos has no server-side delete/update-by-query).
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class CosmosSet<TEntity> : Data.Repository.ISet<TEntity> where TEntity : class, IEntity
{
    private readonly CosmosUnitOfWork _unitOfWork;
    private readonly Container _container;
    private readonly CosmosEntityConfiguration _configuration;
    private readonly IQueryable<TEntity> _queryable;

    internal CosmosSet(CosmosUnitOfWork unitOfWork, Container container)
    {
        _unitOfWork = unitOfWork;
        _container = container;
        _configuration = unitOfWork.Configuration<TEntity>();
        _queryable = container.GetItemLinqQueryable<TEntity>(allowSynchronousQueryExecution: true);
    }

    // ---------------------------------------------------------------- IQueryable
    public Type ElementType => _queryable.ElementType;
    public Expression Expression => _queryable.Expression;
    public IQueryProvider Provider => _queryable.Provider;
    public IEnumerator<TEntity> GetEnumerator() => _queryable.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _queryable.GetEnumerator();

    // ---------------------------------------------------------------- reads
    public IEnumerable<TEntity> Execute() => _queryable.ToList();

    public TEntity? Find<TKey>(TKey key) => FindAsync(key).GetAwaiter().GetResult();

    public async Task<TEntity?> FindAsync<TKey>(TKey key, CancellationToken cancellationToken = default)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE c.id = @id").WithParameter("@id", key?.ToString());
        using var iterator = _container.GetItemQueryIterator<TEntity>(query);
        while (iterator.HasMoreResults)
        {
            foreach (var item in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
            {
                return item;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- entity writes (staged → commit)
    public void Insert(TEntity item) => _unitOfWork.StageInsert(item);

    public Task InsertAsync(TEntity item, CancellationToken cancellationToken = default)
    {
        _unitOfWork.StageInsert(item);
        return Task.CompletedTask;
    }

    /// <summary>ANDs the global filter into a set-based write (a tenant-scoped delete stays tenant-scoped).</summary>
    private Expression<Func<TEntity, bool>> Scoped(Expression<Func<TEntity, bool>> filter) =>
        _unitOfWork.GlobalFilter<TEntity>() is { } global ? filter.AndAlso(global) : filter;

    // ---------------------------------------------------------------- set-based writes (immediate)
    public long DeleteMany(Expression<Func<TEntity, bool>> filter) => DeleteManyAsync(filter).GetAwaiter().GetResult();

    public async Task<long> DeleteManyAsync(Expression<Func<TEntity, bool>> filter, CancellationToken cancellationToken = default)
    {
        var matches = await MaterializeAsync(_queryable.Where(Scoped(filter)), cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(matches.Select(item => _container.DeleteItemStreamAsync(
                _configuration.GetId(item), _configuration.GetPartitionKey(item), cancellationToken: cancellationToken)))
            .ConfigureAwait(false);
        return matches.Count;
    }

    public long UpdateMany(Expression<Func<TEntity, bool>> filter, Expression<Func<TEntity, TEntity>> updateExpression) =>
        UpdateManyAsync(filter, updateExpression).GetAwaiter().GetResult();

    public async Task<long> UpdateManyAsync(Expression<Func<TEntity, bool>> filter,
        Expression<Func<TEntity, TEntity>> updateExpression, CancellationToken cancellationToken = default)
    {
        var patch = CosmosPatch.Build(updateExpression);
        var matches = await MaterializeAsync(_queryable.Where(Scoped(filter)), cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(matches.Select(item => _container.PatchItemAsync<TEntity>(
                _configuration.GetId(item), _configuration.GetPartitionKey(item), patch, cancellationToken: cancellationToken)))
            .ConfigureAwait(false);
        return matches.Count;
    }

    private static async Task<List<TEntity>> MaterializeAsync(IQueryable<TEntity> query, CancellationToken cancellationToken)
    {
        var results = new List<TEntity>();
        using var iterator = query.ToFeedIterator();
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false));
        }

        return results;
    }
}
