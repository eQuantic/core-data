using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.MongoDb.Tests;

/// <summary>A pipeline run with its steps: the parent of a shaped include (the steps live in their own collection).</summary>
public sealed class ShapeRun : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public List<ShapeStep>? Steps { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A run's step, holding the foreign key back to its run by convention (<c>ShapeRunId</c>).</summary>
public sealed class ShapeStep : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ShapeRunId { get; set; } = "";

    public string Status { get; set; } = "";

    public DateTime StartedAt { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A package with its versions: the parent of a relation count.</summary>
public sealed class ShapePackage : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public List<ShapeVersion>? Versions { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A package's version, soft-deleted by a date.</summary>
public sealed class ShapeVersion : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ShapePackageId { get; set; } = "";

    public string Label { get; set; } = "";

    public DateTime? DeletedAt { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A board card whose position may be unset.</summary>
public sealed class ShapeCard : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Board { get; set; } = "";

    public int? Position { get; set; }

    public int Rank { get; set; }

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}

/// <summary>A comment recorded against an issue of a repository.</summary>
public sealed class ShapeComment : IEntity<string>
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Repository { get; set; } = "";

    public int IssueNumber { get; set; }

    public string Body { get; set; } = "";

    public string GetKey() => Id;

    public void SetKey(string key) => Id = key;
}
