using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Dapper;
using eQuantic.Core.Data.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.Benchmarks;

/// <summary>
///     End-to-end write scenarios against a real PostgreSQL. Raw Npgsql is the baseline; every stack writes the
///     same rows. Inserted rows land in categories the read scenarios never touch, so scenario data stays
///     stable across the run. The batch scenario notes one deliberate asymmetry: Dapper's idiomatic
///     <c>ExecuteAsync(sql, list)</c> issues one statement per element, while raw Npgsql, EF Core and eQuantic
///     all batch — the table shows what each stack's <b>idiomatic</b> usage costs.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[HideColumns("Error", "StdDev", "RatioSD", "Gen0", "Gen1", "Gen2")]
public class CompareWriteBenchmarks
{
    private CompareStacks _stacks = null!;

    [GlobalSetup]
    public void Setup() => _stacks = new CompareStacks();

    [GlobalCleanup]
    public void Cleanup() => _stacks.Dispose();

    private static BenchProduct NewProduct(string category) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Inserted",
        Category = category,
        Price = 9.90m,
        Quantity = 1,
        CreatedAt = DateTime.UtcNow,
    };

    // ---------------------------------------------------------------- single insert

    [BenchmarkCategory("5. insert 1"), Benchmark(Baseline = true, Description = "raw Npgsql")]
    public async Task<int> InsertOne_Raw()
    {
        var product = NewProduct("inserted-raw");
        await using var command = _stacks.DataSource.CreateCommand(
            $"INSERT INTO {BenchmarkEnvironment.Table} (id, name, category, price, quantity, created_at) VALUES ($1, $2, $3, $4, $5, $6)");
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = product.Id });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = product.Name });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = product.Category });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = product.Price });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = product.Quantity });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = product.CreatedAt });
        return await command.ExecuteNonQueryAsync();
    }

    [BenchmarkCategory("5. insert 1"), Benchmark(Description = "Dapper")]
    public async Task<int> InsertOne_Dapper()
    {
        await using var connection = await _stacks.DataSource.OpenConnectionAsync();
        return await connection.ExecuteAsync(
            $"INSERT INTO {BenchmarkEnvironment.Table} (id, name, category, price, quantity, created_at) " +
            "VALUES (@Id, @Name, @Category, @Price, @Quantity, @CreatedAt)",
            NewProduct("inserted-dapper"));
    }

    [BenchmarkCategory("5. insert 1"), Benchmark(Description = "EF Core")]
    public async Task<int> InsertOne_EfCore()
    {
        await using var context = _stacks.EfFactory.CreateDbContext();
        context.Products.Add(NewProduct("inserted-ef"));
        return await context.SaveChangesAsync();
    }

    [BenchmarkCategory("5. insert 1"), Benchmark(Description = "eQuantic")]
    public async Task<int> InsertOne_Equantic()
    {
        using var scope = _stacks.Equantic.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAsyncRepository<BenchProduct, Guid>>();
        await repository.AddAsync(NewProduct("inserted-equantic"));
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync();
    }

    // ---------------------------------------------------------------- batch insert (100 rows, one commit)

    [BenchmarkCategory("6. insert 100"), Benchmark(Baseline = true, Description = "raw Npgsql (batch)")]
    public async Task<int> InsertBatch_Raw()
    {
        await using var connection = await _stacks.DataSource.OpenConnectionAsync();
        await using var batch = connection.CreateBatch();
        for (var index = 0; index < 100; index++)
        {
            var product = NewProduct("batch-raw");
            var command = batch.CreateBatchCommand();
            command.CommandText =
                $"INSERT INTO {BenchmarkEnvironment.Table} (id, name, category, price, quantity, created_at) VALUES ($1, $2, $3, $4, $5, $6)";
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = product.Id });
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = product.Name });
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = product.Category });
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = product.Price });
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = product.Quantity });
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = product.CreatedAt });
            batch.BatchCommands.Add(command);
        }

        return await batch.ExecuteNonQueryAsync();
    }

    [BenchmarkCategory("6. insert 100"), Benchmark(Description = "Dapper (per-row)")]
    public async Task<int> InsertBatch_Dapper()
    {
        var products = Enumerable.Range(0, 100).Select(_ => NewProduct("batch-dapper")).ToList();
        await using var connection = await _stacks.DataSource.OpenConnectionAsync();
        return await connection.ExecuteAsync(
            $"INSERT INTO {BenchmarkEnvironment.Table} (id, name, category, price, quantity, created_at) " +
            "VALUES (@Id, @Name, @Category, @Price, @Quantity, @CreatedAt)",
            products);
    }

    [BenchmarkCategory("6. insert 100"), Benchmark(Description = "EF Core")]
    public async Task<int> InsertBatch_EfCore()
    {
        await using var context = _stacks.EfFactory.CreateDbContext();
        context.Products.AddRange(Enumerable.Range(0, 100).Select(_ => NewProduct("batch-ef")));
        return await context.SaveChangesAsync();
    }

    [BenchmarkCategory("6. insert 100"), Benchmark(Description = "eQuantic")]
    public async Task<int> InsertBatch_Equantic()
    {
        using var scope = _stacks.Equantic.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAsyncRepository<BenchProduct, Guid>>();
        for (var index = 0; index < 100; index++)
        {
            await repository.AddAsync(NewProduct("batch-equantic"));
        }

        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync();
    }

    // ---------------------------------------------------------------- set-based update (500 rows, server-side)

    [BenchmarkCategory("7. set update 500"), Benchmark(Baseline = true, Description = "raw Npgsql")]
    public async Task<int> SetUpdate_Raw()
    {
        await using var command = _stacks.DataSource.CreateCommand(
            $"UPDATE {BenchmarkEnvironment.Table} SET quantity = $1 WHERE category = $2");
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = 42 });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = BenchmarkEnvironment.UpdateCategory });
        return await command.ExecuteNonQueryAsync();
    }

    [BenchmarkCategory("7. set update 500"), Benchmark(Description = "Dapper")]
    public async Task<int> SetUpdate_Dapper()
    {
        await using var connection = await _stacks.DataSource.OpenConnectionAsync();
        return await connection.ExecuteAsync(
            $"UPDATE {BenchmarkEnvironment.Table} SET quantity = @quantity WHERE category = @category",
            new { quantity = 42, category = BenchmarkEnvironment.UpdateCategory });
    }

    [BenchmarkCategory("7. set update 500"), Benchmark(Description = "EF Core")]
    public async Task<int> SetUpdate_EfCore()
    {
        await using var context = _stacks.EfFactory.CreateDbContext();
        return await context.Products
            .Where(p => p.Category == BenchmarkEnvironment.UpdateCategory)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.Quantity, 42));
    }

    [BenchmarkCategory("7. set update 500"), Benchmark(Description = "eQuantic")]
    public async Task<long> SetUpdate_Equantic()
    {
        using var scope = _stacks.Equantic.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAsyncRepository<BenchProduct, Guid>>();
        return await repository.UpdateManyAsync(
            p => p.Category == BenchmarkEnvironment.UpdateCategory,
            p => new BenchProduct { Quantity = 42 });
    }
}
