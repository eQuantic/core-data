using eQuantic.Core.Data.PostgreSql.Extensions;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Write;
using Microsoft.Extensions.DependencyInjection;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>
///     Pins the AOT-friendly registration surface — closed-generic repositories and the explicit unit-of-work
///     factory — that lets the stack resolve without the open-generic-over-value-type instantiation NativeAOT
///     cannot do. These run under the JIT (they prove the API shape and resolution); the native round-trip is
///     proven by <c>samples/AotProbe</c>.
/// </summary>
[TestFixture]
public sealed class PostgreSqlAotRegistrationTests : PostgreSqlIntegrationTest
{
    [Test]
    public async Task Closed_generic_registration_resolves_a_working_repository()
    {
        using var db = await NewSchemaAsync(services => services.AddPostgreSqlRepository<Article, Guid>());
        var repo = db.Resolve<IAsyncRepository<Article, Guid>>();

        await repo.AddAsync(new Article { Title = "aot-registered" });
        await Uow(db).CommitAsync();

        var found = await repo.GetFilteredAsync(x => x.Title == "aot-registered");
        Assert.That(found.Single().Title, Is.EqualTo("aot-registered"),
            "the closed-generic registration resolves the same working repository the open-generic one does");
    }

    [Test]
    public void The_explicit_unit_of_work_factory_registers_the_facades()
    {
        var services = new ServiceCollection();
        services.AddPostgreSqlUnitOfWork();

        Assert.That(services.Any(descriptor => descriptor.ServiceType == typeof(IUnitOfWork)), Is.True);
        Assert.That(services.Any(descriptor => descriptor.ServiceType == typeof(IQueryableUnitOfWork)), Is.True);
        Assert.That(services.All(descriptor => descriptor.ServiceType != typeof(IRepository<,>)), Is.True,
            "the unit-of-work registration adds no open-generic repository descriptors (the AOT blocker)");
    }

    [Test]
    public void Closed_generic_registration_resolves_the_write_capabilities()
    {
        var services = new ServiceCollection();
        services.AddPostgreSqlDatabase("Host=unused", TestSchema.Configure);
        services.AddPostgreSqlRepository<Article, Guid>();
        Assert.That(services.All(descriptor => !descriptor.ServiceType.IsGenericTypeDefinition), Is.True,
            "nothing open-generic is registered on the AOT path");

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAsyncRepository<Article, Guid>>();
        Assert.That(scope.ServiceProvider.GetRequiredService<IUpsertRepository<Article, Guid>>(), Is.InstanceOf(repository.GetType()));
        Assert.That(scope.ServiceProvider.GetRequiredService<IReturningUpdateRepository<Article, Guid>>(), Is.InstanceOf(repository.GetType()));
        Assert.That(scope.ServiceProvider.GetRequiredService<IPartialModifyRepository<Article, Guid>>(), Is.InstanceOf(repository.GetType()));
        Assert.That(repository, Is.InstanceOf<IUpsertRepository<Article, Guid>>(), "the repository itself casts to the capability");
    }
}
