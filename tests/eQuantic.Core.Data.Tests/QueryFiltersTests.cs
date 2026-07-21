using eQuantic.Core.Data.Repository;

namespace eQuantic.Core.Data.Tests;

/// <summary>
///     Unit tests for the <see cref="QueryFilters" /> registry — pure, no store: fixed filters, per-request
///     factories receiving the scope's service provider (the multi-tenant path), and the null default.
/// </summary>
[TestFixture]
public sealed class QueryFiltersTests
{
    private sealed class Tenant
    {
        public int Id { get; init; }
    }

    private sealed class FakeProvider(object? tenant) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(Tenant) ? tenant : null;
    }

    [Test]
    public void A_fixed_filter_resolves_for_its_entity()
    {
        var filters = new QueryFilters().For<Sample>(x => x.IsActive);

        var filter = filters.FilterFor<Sample>(new FakeProvider(null));

        Assert.That(filter, Is.Not.Null);
        Assert.That(filter!.Compile()(new Sample { IsActive = true }), Is.True);
    }

    [Test]
    public void A_per_request_factory_receives_the_scopes_provider()
    {
        var filters = new QueryFilters().For<Sample>(services =>
        {
            var tenant = (Tenant)services.GetService(typeof(Tenant))!;
            return x => x.TenantId == tenant.Id;
        });

        var filter = filters.FilterFor<Sample>(new FakeProvider(new Tenant { Id = 7 }))!.Compile();

        Assert.That(filter(new Sample { TenantId = 7 }), Is.True);
        Assert.That(filter(new Sample { TenantId = 8 }), Is.False);
    }

    [Test]
    public void A_factory_returning_null_applies_no_filter_for_the_request()
    {
        var filters = new QueryFilters().For<Sample>(_ => null);

        Assert.That(filters.FilterFor<Sample>(new FakeProvider(null)), Is.Null);
    }

    [Test]
    public void An_unregistered_entity_resolves_null()
    {
        Assert.That(new QueryFilters().FilterFor<Sample>(new FakeProvider(null)), Is.Null);
    }
}
