using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.Cassandra.Tests;

/// <summary>A pipeline run with its steps: the parent of a shaped include.</summary>
public sealed class ShapeRun : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public List<ShapeStep>? Steps { get; set; }

    public List<ShapeTag>? Tags { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A run's step, holding the foreign key back to its run by convention (<c>ShapeRunId</c>).</summary>
public sealed class ShapeStep : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ShapeRunId { get; set; }

    public string Status { get; set; } = "";

    public DateTime StartedAt { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A package with its versions: the parent of a relation count.</summary>
public sealed class ShapePackage : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public List<ShapeVersion>? Versions { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A package's version, soft-deleted by a date (not by the lifecycle interfaces).</summary>
public sealed class ShapeVersion : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ShapePackageId { get; set; }

    public string Label { get; set; } = "";

    public DateTime? DeletedAt { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A board card whose position may be unset: sorted with nulls first or last, and paged without a total.</summary>
public sealed class ShapeCard : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Board { get; set; } = "";

    public int? Position { get; set; }

    public int Rank { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A comment recorded against an issue of a repository: read for its distinct (repository, issue) pairs.</summary>
public sealed class ShapeComment : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Repository { get; set; } = "";

    public int IssueNumber { get; set; }

    public string Body { get; set; } = "";

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A tag of a run, in its run's partition, clustered by its name (text, kept in UTF-8's byte order).</summary>
public sealed class ShapeTag : IEntity<Guid>
{
    public Guid ShapeRunId { get; set; }

    public string Name { get; set; } = "";

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A support queue with its tickets: the parent of an include whose foreign key the related table does not key.</summary>
public sealed class ShapeQueue : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public List<ShapeTicket>? Tickets { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A ticket, partitioned by the desk that works it: its queue's key (<c>ShapeQueueId</c>) is a regular column.</summary>
public sealed class ShapeTicket : IEntity<Guid>
{
    public string Desk { get; set; } = "";

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ShapeQueueId { get; set; }

    public string Title { get; set; } = "";

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A zone, the key of a region (stored as its number, as an enum without <c>[EnumType]</c> is).</summary>
public enum ShapeZone
{
    North = 1,
    South = 2,
}

/// <summary>A region keyed by its zone, with the offices in it.</summary>
public sealed class ShapeRegion : IEntity<ShapeZone>
{
    public ShapeZone Id { get; set; }

    public string Name { get; set; } = "";

    public List<ShapeOffice>? Offices { get; set; }

    public ShapeZone GetKey() => Id;

    public void SetKey(ShapeZone key) => Id = key;
}

/// <summary>An office, partitioned by its region's zone.</summary>
public sealed class ShapeOffice : IEntity<Guid>
{
    public ShapeZone ShapeRegionId { get; set; }

    public Guid Id { get; set; } = Guid.NewGuid();

    public string City { get; set; } = "";

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>
///     The read-shape entities' mapping: each related table is partitioned by its foreign key, so a run's steps, a
///     package's versions, a board's cards and a repository's comments each live in one partition, clustered in the
///     order they are read. A ticket is the exception: it is partitioned by its desk, so its queue's key is a regular column.
/// </summary>
internal static class ReadShapesModel
{
    public static void Configure(CassandraModelBuilder model) => model
        .Entity<ShapeRun>(entity => entity.PartitionKey(x => x.Id))
        .Entity<ShapeStep>(entity => entity
            .PartitionKey(x => x.ShapeRunId)
            .ClusteringKey(x => x.StartedAt, descending: true)
            .ClusteringKey(x => x.Id)
            .Key(x => x.Id))
        .Entity<ShapePackage>(entity => entity.PartitionKey(x => x.Id))
        .Entity<ShapeVersion>(entity => entity
            .PartitionKey(x => x.ShapePackageId)
            .ClusteringKey(x => x.Id)
            .Key(x => x.Id))
        .Entity<ShapeCard>(entity => entity
            .PartitionKey(x => x.Board)
            .ClusteringKey(x => x.Rank)
            .ClusteringKey(x => x.Id)
            .Key(x => x.Id))
        .Entity<ShapeComment>(entity => entity
            .PartitionKey(x => x.Repository)
            .ClusteringKey(x => x.IssueNumber)
            .ClusteringKey(x => x.Id)
            .Key(x => x.Id))
        .Entity<ShapeTag>(entity => entity
            .PartitionKey(x => x.ShapeRunId)
            .ClusteringKey(x => x.Name)
            .ClusteringKey(x => x.Id)
            .Key(x => x.Id))
        .Entity<ShapeRegion>(entity => entity.PartitionKey(x => x.Id))
        .Entity<ShapeOffice>(entity => entity
            .PartitionKey(x => x.ShapeRegionId)
            .ClusteringKey(x => x.Id)
            .Key(x => x.Id))
        .Entity<ShapeQueue>(entity => entity.PartitionKey(x => x.Id))
        .Entity<ShapeTicket>(entity => entity
            .PartitionKey(x => x.Desk)
            .ClusteringKey(x => x.Id)
            .Key(x => x.Id));

    /// <summary>The tables, created by the executor (a discovered migration would run for every test keyspace).</summary>
    public static readonly IReadOnlyList<MigrationOperation> Tables =
    [
        new EnsureCollectionOperation(typeof(ShapeRun)),
        new EnsureCollectionOperation(typeof(ShapeStep)),
        new EnsureCollectionOperation(typeof(ShapePackage)),
        new EnsureCollectionOperation(typeof(ShapeVersion)),
        new EnsureCollectionOperation(typeof(ShapeCard)),
        new EnsureCollectionOperation(typeof(ShapeComment)),
        new EnsureCollectionOperation(typeof(ShapeTag)),
        new EnsureCollectionOperation(typeof(ShapeRegion)),
        new EnsureCollectionOperation(typeof(ShapeOffice)),
        new EnsureCollectionOperation(typeof(ShapeQueue)),
        new EnsureCollectionOperation(typeof(ShapeTicket)),
    ];
}
