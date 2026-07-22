using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Dapper;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.Benchmarks;

/// <summary>
///     End-to-end read scenarios against a real PostgreSQL, one benchmark per stack per scenario. Raw Npgsql is
///     the baseline (the floor any abstraction pays against); EF Core reads with <c>AsNoTracking</c> over a
///     pooled context factory; Dapper runs the equivalent hand-written SQL; eQuantic resolves a repository from
///     a fresh scope per operation, the way a request would.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[HideColumns("Error", "StdDev", "RatioSD", "Gen0", "Gen1", "Gen2")]
public class CompareReadBenchmarks
{
    private CompareStacks _stacks = null!;

    [GlobalSetup]
    public void Setup() => _stacks = new CompareStacks();

    [GlobalCleanup]
    public void Cleanup() => _stacks.Dispose();

    // ---------------------------------------------------------------- point read by key

    [BenchmarkCategory("1. point read"), Benchmark(Baseline = true, Description = "raw Npgsql")]
    public async Task<BenchProduct?> PointRead_Raw()
    {
        await using var command = _stacks.DataSource.CreateCommand(
            $"SELECT id, name, category, price, quantity, created_at FROM {BenchmarkEnvironment.Table} WHERE id = $1");
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = BenchmarkEnvironment.PointReadId });
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? Materialize(reader) : null;
    }

    [BenchmarkCategory("1. point read"), Benchmark(Description = "Dapper")]
    public async Task<BenchProduct?> PointRead_Dapper()
    {
        await using var connection = await _stacks.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleOrDefaultAsync<BenchProduct>(
            $"SELECT id, name, category, price, quantity, created_at FROM {BenchmarkEnvironment.Table} WHERE id = @id",
            new { id = BenchmarkEnvironment.PointReadId });
    }

    [BenchmarkCategory("1. point read"), Benchmark(Description = "EF Core")]
    public async Task<BenchProduct?> PointRead_EfCore()
    {
        await using var context = _stacks.EfFactory.CreateDbContext();
        return await context.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == BenchmarkEnvironment.PointReadId);
    }

    [BenchmarkCategory("1. point read"), Benchmark(Description = "eQuantic")]
    public async Task<BenchProduct?> PointRead_Equantic()
    {
        using var scope = _stacks.Equantic.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAsyncRepository<BenchProduct, Guid>>();
        return await repository.GetAsync(BenchmarkEnvironment.PointReadId);
    }

    // ---------------------------------------------------------------- filtered set (500 rows)

    [BenchmarkCategory("2. filtered 500"), Benchmark(Baseline = true, Description = "raw Npgsql")]
    public async Task<List<BenchProduct>> Filtered_Raw()
    {
        await using var command = _stacks.DataSource.CreateCommand(
            $"SELECT id, name, category, price, quantity, created_at FROM {BenchmarkEnvironment.Table} WHERE category = $1 ORDER BY name");
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = BenchmarkEnvironment.HotCategory });
        await using var reader = await command.ExecuteReaderAsync();
        var results = new List<BenchProduct>();
        while (await reader.ReadAsync())
        {
            results.Add(Materialize(reader));
        }

        return results;
    }

    [BenchmarkCategory("2. filtered 500"), Benchmark(Description = "Dapper")]
    public async Task<List<BenchProduct>> Filtered_Dapper()
    {
        await using var connection = await _stacks.DataSource.OpenConnectionAsync();
        var rows = await connection.QueryAsync<BenchProduct>(
            $"SELECT id, name, category, price, quantity, created_at FROM {BenchmarkEnvironment.Table} WHERE category = @category ORDER BY name",
            new { category = BenchmarkEnvironment.HotCategory });
        return rows.AsList();
    }

    [BenchmarkCategory("2. filtered 500"), Benchmark(Description = "EF Core")]
    public async Task<List<BenchProduct>> Filtered_EfCore()
    {
        await using var context = _stacks.EfFactory.CreateDbContext();
        return await context.Products.AsNoTracking()
            .Where(p => p.Category == BenchmarkEnvironment.HotCategory)
            .OrderBy(p => p.Name)
            .ToListAsync();
    }

    [BenchmarkCategory("2. filtered 500"), Benchmark(Description = "eQuantic")]
    public async Task<List<BenchProduct>> Filtered_Equantic()
    {
        using var scope = _stacks.Equantic.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAsyncRepository<BenchProduct, Guid>>();
        var rows = await repository.GetFilteredAsync(
            p => p.Category == BenchmarkEnvironment.HotCategory,
            new QueryOptions<BenchProduct>().OrderBy(p => p.Name));
        return rows.ToList();
    }

    // ---------------------------------------------------------------- projection (500 rows, 3 columns)

    [BenchmarkCategory("3. projection 500"), Benchmark(Baseline = true, Description = "raw Npgsql")]
    public async Task<List<ProductRow>> Projection_Raw()
    {
        await using var command = _stacks.DataSource.CreateCommand(
            $"SELECT id, name, price FROM {BenchmarkEnvironment.Table} WHERE category = $1 ORDER BY name");
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = BenchmarkEnvironment.HotCategory });
        await using var reader = await command.ExecuteReaderAsync();
        var results = new List<ProductRow>();
        while (await reader.ReadAsync())
        {
            results.Add(new ProductRow(reader.GetGuid(0), reader.GetString(1), reader.GetDecimal(2)));
        }

        return results;
    }

    [BenchmarkCategory("3. projection 500"), Benchmark(Description = "Dapper")]
    public async Task<List<ProductRow>> Projection_Dapper()
    {
        await using var connection = await _stacks.DataSource.OpenConnectionAsync();
        var rows = await connection.QueryAsync<ProductRow>(
            $"SELECT id, name, price FROM {BenchmarkEnvironment.Table} WHERE category = @category ORDER BY name",
            new { category = BenchmarkEnvironment.HotCategory });
        return rows.AsList();
    }

    [BenchmarkCategory("3. projection 500"), Benchmark(Description = "EF Core")]
    public async Task<List<ProductRow>> Projection_EfCore()
    {
        await using var context = _stacks.EfFactory.CreateDbContext();
        return await context.Products.AsNoTracking()
            .Where(p => p.Category == BenchmarkEnvironment.HotCategory)
            .OrderBy(p => p.Name)
            .Select(p => new ProductRow(p.Id, p.Name, p.Price))
            .ToListAsync();
    }

    [BenchmarkCategory("3. projection 500"), Benchmark(Description = "eQuantic")]
    public async Task<List<ProductRow>> Projection_Equantic()
    {
        using var scope = _stacks.Equantic.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAsyncRepository<BenchProduct, Guid>>();
        var rows = await repository.GetMappedAsync(
            p => new ProductRow(p.Id, p.Name, p.Price),
            new QueryOptions<BenchProduct>()
                .Where(p => p.Category == BenchmarkEnvironment.HotCategory)
                .OrderBy(p => p.Name));
        return rows.ToList();
    }

    // ---------------------------------------------------------------- offset page (count + 20 rows)

    [BenchmarkCategory("4. page 20"), Benchmark(Baseline = true, Description = "raw Npgsql")]
    public async Task<(long Total, List<BenchProduct> Items)> Page_Raw()
    {
        await using var count = _stacks.DataSource.CreateCommand(
            $"SELECT COUNT(*) FROM {BenchmarkEnvironment.Table} WHERE category = $1");
        count.Parameters.Add(new Npgsql.NpgsqlParameter { Value = BenchmarkEnvironment.HotCategory });
        var total = (long)(await count.ExecuteScalarAsync())!;

        await using var page = _stacks.DataSource.CreateCommand(
            $"SELECT id, name, category, price, quantity, created_at FROM {BenchmarkEnvironment.Table} " +
            "WHERE category = $1 ORDER BY name LIMIT 20 OFFSET 80");
        page.Parameters.Add(new Npgsql.NpgsqlParameter { Value = BenchmarkEnvironment.HotCategory });
        await using var reader = await page.ExecuteReaderAsync();
        var items = new List<BenchProduct>();
        while (await reader.ReadAsync())
        {
            items.Add(Materialize(reader));
        }

        return (total, items);
    }

    [BenchmarkCategory("4. page 20"), Benchmark(Description = "Dapper")]
    public async Task<(long Total, List<BenchProduct> Items)> Page_Dapper()
    {
        await using var connection = await _stacks.DataSource.OpenConnectionAsync();
        var total = await connection.ExecuteScalarAsync<long>(
            $"SELECT COUNT(*) FROM {BenchmarkEnvironment.Table} WHERE category = @category",
            new { category = BenchmarkEnvironment.HotCategory });
        var items = await connection.QueryAsync<BenchProduct>(
            $"SELECT id, name, category, price, quantity, created_at FROM {BenchmarkEnvironment.Table} " +
            "WHERE category = @category ORDER BY name LIMIT 20 OFFSET 80",
            new { category = BenchmarkEnvironment.HotCategory });
        return (total, items.AsList());
    }

    [BenchmarkCategory("4. page 20"), Benchmark(Description = "EF Core")]
    public async Task<(long Total, List<BenchProduct> Items)> Page_EfCore()
    {
        await using var context = _stacks.EfFactory.CreateDbContext();
        var query = context.Products.AsNoTracking().Where(p => p.Category == BenchmarkEnvironment.HotCategory);
        var total = await query.LongCountAsync();
        var items = await query.OrderBy(p => p.Name).Skip(80).Take(20).ToListAsync();
        return (total, items);
    }

    [BenchmarkCategory("4. page 20"), Benchmark(Description = "eQuantic")]
    public async Task<(long Total, List<BenchProduct> Items)> Page_Equantic()
    {
        using var scope = _stacks.Equantic.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAsyncRepository<BenchProduct, Guid>>();
        var page = await repository.GetPagedAsync(new PageRequest(pageIndex: 5, pageSize: 20),
            new QueryOptions<BenchProduct>()
                .Where(p => p.Category == BenchmarkEnvironment.HotCategory)
                .OrderBy(p => p.Name));
        return (page.TotalCount, page.Items.ToList());
    }

    private static BenchProduct Materialize(System.Data.Common.DbDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        Name = reader.GetString(1),
        Category = reader.GetString(2),
        Price = reader.GetDecimal(3),
        Quantity = reader.GetInt32(4),
        CreatedAt = reader.GetDateTime(5),
    };
}
