using eQuantic.Core.Data.Repository;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace eQuantic.Core.Data.Benchmarks;

/// <summary>The entity every compared stack maps — one shape, four implementations.</summary>
public sealed class BenchProduct : IEntity<Guid>
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
    public int Quantity { get; set; }
    public DateTime CreatedAt { get; set; }

    public Guid GetKey() => Id;
    public void SetKey(Guid key) => Id = key;
}

/// <summary>The projection shape used by the projection scenario.</summary>
public sealed record ProductRow(Guid Id, string Name, decimal Price);

/// <summary>The EF Core context mapping the same table with the same column names.</summary>
public sealed class BenchDbContext(DbContextOptions<BenchDbContext> options) : DbContext(options)
{
    public DbSet<BenchProduct> Products => Set<BenchProduct>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<BenchProduct>(entity =>
        {
            entity.ToTable(BenchmarkEnvironment.Table);
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.Category).HasColumnName("category");
            entity.Property(x => x.Price).HasColumnName("price");
            entity.Property(x => x.Quantity).HasColumnName("quantity");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
        });
}

/// <summary>
///     The shared benchmark environment. <c>Program.Main</c> (the parent process) starts one PostgreSQL
///     container, seeds it and exports the connection string; every BenchmarkDotNet child process reads it back
///     here. Ids and categories are deterministic so parent and children agree without further coordination.
/// </summary>
public static class BenchmarkEnvironment
{
    public const string Table = "bench_products";
    public const string ConnectionVariable = "EQUANTIC_BENCH_CONNECTION";
    public const int Rows = 10_000;
    public const int Categories = 20;

    /// <summary>The category the read scenarios filter on (500 of the 10 000 seeded rows).</summary>
    public const string HotCategory = "category-07";

    /// <summary>The category the set-based update rewrites (500 rows, disjoint from reads).</summary>
    public const string UpdateCategory = "category-03";

    /// <summary>The id the point-read scenario fetches (the middle seeded row).</summary>
    public static readonly Guid PointReadId = IdFor(Rows / 2);

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionVariable)
        ?? throw new InvalidOperationException(
            $"Run through Program.Main — it starts the PostgreSQL container and exports {ConnectionVariable}.");

    public static Guid IdFor(int index)
    {
        var bytes = new byte[16];
        new Random(7_000_000 + index).NextBytes(bytes);
        return new Guid(bytes);
    }

    public static string CategoryFor(int index) => $"category-{index % Categories:D2}";

    /// <summary>Creates the table (fresh) and seeds the deterministic data set. Parent process only.</summary>
    public static async Task SeedAsync(string connectionString)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);

        await using (var create = dataSource.CreateCommand(
                         $"""
                          DROP TABLE IF EXISTS {Table};
                          CREATE TABLE {Table} (
                              id uuid PRIMARY KEY,
                              name text NOT NULL,
                              category text NOT NULL,
                              price numeric NOT NULL,
                              quantity int NOT NULL,
                              created_at timestamptz NOT NULL);
                          CREATE INDEX ix_{Table}_category ON {Table} (category);
                          """))
        {
            await create.ExecuteNonQueryAsync();
        }

        var stamp = DateTime.UtcNow;
        for (var offset = 0; offset < Rows; offset += 1000)
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var batch = connection.CreateBatch();
            for (var index = offset; index < offset + 1000; index++)
            {
                var command = batch.CreateBatchCommand();
                command.CommandText =
                    $"INSERT INTO {Table} (id, name, category, price, quantity, created_at) VALUES ($1, $2, $3, $4, $5, $6)";
                command.Parameters.Add(new NpgsqlParameter { Value = IdFor(index) });
                command.Parameters.Add(new NpgsqlParameter { Value = $"Product {index:D5}" });
                command.Parameters.Add(new NpgsqlParameter { Value = CategoryFor(index) });
                command.Parameters.Add(new NpgsqlParameter { Value = 10m + index % 500 });
                command.Parameters.Add(new NpgsqlParameter { Value = index % 100 });
                command.Parameters.Add(new NpgsqlParameter { Value = stamp });
                batch.BatchCommands.Add(command);
            }

            await batch.ExecuteNonQueryAsync();
        }
    }
}
