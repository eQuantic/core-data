using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace eQuantic.Core.Data.Repository;

/// <summary>
///     The global query filters — predicates a provider ANDs into <b>every</b> read (and set-based write) of an
///     entity type, the way EF Core's <c>HasQueryFilter</c> scopes multi-tenant data, soft deletes or row-level
///     visibility. Register one instance as a singleton
///     (<c>services.AddSingleton(new QueryFilters().For&lt;Order&gt;(...))</c>); the unit of work picks it up from
///     its scope's service provider. A filter can be a fixed predicate or a <b>per-request factory</b> that
///     receives the scoped <see cref="IServiceProvider" /> — resolve the current tenant (or user, or feature
///     flag) there and return the predicate for this request. A query opts out with
///     <see cref="Options.QueryOptions{TEntity}.IgnoringQueryFilters" />; set-based writes never opt out (a
///     tenant-scoped delete stays tenant-scoped).
/// </summary>
public sealed class QueryFilters
{
    private readonly Dictionary<Type, Func<IServiceProvider, LambdaExpression?>> _factories = new();

    /// <summary>Registers a fixed global filter for <typeparamref name="TEntity" />.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="filter">The predicate ANDed into every query of the entity.</param>
    /// <returns>The same instance for chaining.</returns>
    public QueryFilters For<TEntity>(Expression<Func<TEntity, bool>> filter)
        where TEntity : class
    {
        if (filter is null)
        {
            throw new ArgumentNullException(nameof(filter));
        }

        _factories[typeof(TEntity)] = _ => filter;
        return this;
    }

    /// <summary>
    ///     Registers a per-request global filter for <typeparamref name="TEntity" />: the factory runs on every
    ///     query with the scope's service provider — resolve the current tenant there. Returning <c>null</c>
    ///     applies no filter for that request.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="filter">The per-request predicate factory.</param>
    /// <returns>The same instance for chaining.</returns>
    public QueryFilters For<TEntity>(Func<IServiceProvider, Expression<Func<TEntity, bool>>?> filter)
        where TEntity : class
    {
        if (filter is null)
        {
            throw new ArgumentNullException(nameof(filter));
        }

        _factories[typeof(TEntity)] = services => filter(services);
        return this;
    }

    /// <summary>Resolves the global filter for <typeparamref name="TEntity" /> in the given scope, or <c>null</c> when none applies.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="services">The scope's service provider (handed to per-request factories).</param>
    public Expression<Func<TEntity, bool>>? FilterFor<TEntity>(IServiceProvider services)
        where TEntity : class =>
        _factories.TryGetValue(typeof(TEntity), out var factory)
            ? (Expression<Func<TEntity, bool>>?)factory(services)
            : null;
}
