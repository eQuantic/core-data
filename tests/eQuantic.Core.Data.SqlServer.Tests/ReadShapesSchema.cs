using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.SqlServer.Tests;

/// <summary>A pipeline run with its steps: the parent of a shaped include.</summary>
public sealed class ShapeRun : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public List<ShapeStep>? Steps { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>
///     A run's step, holding the foreign key back to its run by convention (<c>ShapeRunId</c>), the runner it ran on (a
///     reference, by <c>RunnerId</c>) and the lines it logged (a collection): the relations a count's filter tests.
/// </summary>
public sealed class ShapeStep : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ShapeRunId { get; set; }

    public string Status { get; set; } = "";

    public DateTime StartedAt { get; set; }

    public Guid? RunnerId { get; set; }

    public ShapeRunner? Runner { get; set; }

    public List<ShapeLog>? Logs { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A line a step logged, holding the foreign key back to its step by convention (<c>ShapeStepId</c>).</summary>
public sealed class ShapeLog : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ShapeStepId { get; set; }

    public string Text { get; set; } = "";

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A runner a step ran on.</summary>
public sealed class ShapeRunner : IEntity<Guid>
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

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

/// <summary>The read-shape entities' mapping.</summary>
internal static class ReadShapesSchema
{
    public static RelationalModelBuilder Configure(RelationalModelBuilder builder) => builder
        .Entity<ShapeRun>(_ => { })
        .Entity<ShapeStep>(_ => { })
        .Entity<ShapeLog>(_ => { })
        .Entity<ShapeRunner>(_ => { })
        .Entity<ShapePackage>(_ => { })
        .Entity<ShapeVersion>(_ => { })
        .Entity<ShapeCard>(_ => { })
        .Entity<ShapeComment>(_ => { });
}

/// <summary>Creates the read-shape tables.</summary>
[Migration("Read shapes", 2026, 1, 5, 0, 0, 0)]
public sealed class ReadShapesMigration : Data.Migration.Migration
{
    /// <inheritdoc />
    public override void Up(IMigrationBuilder migration) => migration
        .For<ShapeRun>(run => run.EnsureCollection())
        .For<ShapeStep>(step => step.EnsureCollection().Index(x => x.ShapeRunId))
        .For<ShapeLog>(log => log.EnsureCollection().Index(x => x.ShapeStepId))
        .For<ShapeRunner>(runner => runner.EnsureCollection())
        .For<ShapePackage>(package => package.EnsureCollection())
        .For<ShapeVersion>(version => version.EnsureCollection().Index(x => x.ShapePackageId))
        .For<ShapeCard>(card => card.EnsureCollection())
        .For<ShapeComment>(comment => comment.EnsureCollection());
}
