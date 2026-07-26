using System.Collections.Generic;

namespace eQuantic.Core.Data.Repository;

/// <summary>
///     The execution plan a provider builds for a shaped read — what will actually run and at what cost. A plan
///     never lies: everything the store executes is in <see cref="Statement" />, everything the library evaluates
///     client-side is in <see cref="Residual" />, and the cost flags say so explicitly. Obtain one from a
///     repository implementing <see cref="Read.IExplainableRepository{TEntity}" />; building a plan performs no I/O.
/// </summary>
public sealed class QueryPlan
{
    /// <summary>Initializes the plan.</summary>
    /// <param name="provider">The provider name (e.g. <c>Cassandra</c>).</param>
    /// <param name="statement">The statement the store executes, in its native dialect.</param>
    /// <param name="parameters">The bound parameter values, in order.</param>
    /// <param name="residual">Human-readable description of the predicate evaluated client-side, or <c>null</c> when fully pushed down.</param>
    /// <param name="serverSideFiltering">Whether the store filters outside its native access path (e.g. CQL <c>ALLOW FILTERING</c>).</param>
    /// <param name="clientEvaluation">Whether part of the predicate is evaluated client-side over the fetched rows.</param>
    /// <param name="partitionScoped">Whether the query is pinned to a single partition (the store's cheap path).</param>
    /// <param name="notes">Additional provider-specific remarks (gates required, caching, paging strategy).</param>
    public QueryPlan(string provider, string statement, IReadOnlyList<object?> parameters, string? residual,
        bool serverSideFiltering, bool clientEvaluation, bool partitionScoped, IReadOnlyList<string> notes)
    {
        Provider = provider;
        Statement = statement;
        Parameters = parameters;
        Residual = residual;
        ServerSideFiltering = serverSideFiltering;
        ClientEvaluation = clientEvaluation;
        PartitionScoped = partitionScoped;
        Notes = notes;
    }

    /// <summary>The provider name (e.g. <c>Cassandra</c>, <c>MongoDb</c>, <c>CosmosDb</c>).</summary>
    public string Provider { get; }

    /// <summary>The statement the store executes, rendered in its native dialect (CQL, Cosmos SQL, aggregation pipeline).</summary>
    public string Statement { get; }

    /// <summary>The bound parameter values, in statement order.</summary>
    public IReadOnlyList<object?> Parameters { get; }

    /// <summary>The predicate evaluated client-side over the fetched rows, or <c>null</c> when everything is pushed down.</summary>
    public string? Residual { get; }

    /// <summary>Whether the store filters outside its native access path (e.g. CQL <c>ALLOW FILTERING</c>).</summary>
    public bool ServerSideFiltering { get; }

    /// <summary>Whether part of the predicate runs client-side (the query fetches a superset and the library filters it).</summary>
    public bool ClientEvaluation { get; }

    /// <summary>Whether the query is pinned to a single partition — the store's cheap path.</summary>
    public bool PartitionScoped { get; }

    /// <summary>Additional provider-specific remarks (gates required, caching, paging strategy).</summary>
    public IReadOnlyList<string> Notes { get; }
}
