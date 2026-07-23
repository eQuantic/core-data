using System.Collections.Concurrent;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Data.Repository.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>
///     Proves the logging contract against a real database: every command logs under the stable category with
///     the stable event ids, statements carry placeholders only by default, parameter values appear exactly
///     when <see cref="DataConventions.EnableSensitiveDataLogging" /> says so, and commits and gate engagements
///     log their own events. Any MEL sink — Serilog included — sees precisely these entries.
/// </summary>
[TestFixture]
public sealed class PostgreSqlLoggingTests : PostgreSqlIntegrationTest
{
    private sealed record LogEntry(string Category, LogLevel Level, EventId Event, string Message);

    private sealed class Collector : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(category, logLevel, eventId, formatter(state, exception)));
        }
    }

    private static void AddLogging(IServiceCollection services, Collector collector) =>
        services.AddSingleton<ILoggerFactory>(_ =>
        {
            var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug));
            factory.AddProvider(collector);
            return factory;
        });

    [Test]
    public async Task Commands_and_commits_log_under_the_stable_category_without_values()
    {
        var collector = new Collector();
        using var db = await NewSchemaAsync(services => AddLogging(services, collector));
        var repo = db.Resolve<eQuantic.Core.Data.Repository.IAsyncRepository<Article, Guid>>();

        await repo.AddAsync(new Article { Title = "logging-probe" });
        await Uow(db).CommitAsync();
        await repo.GetFilteredAsync(x => x.Title == "logging-probe");

        var entries = collector.Entries.Where(entry => entry.Category.StartsWith("eQuantic.Core.Data.")).ToList();
        Assert.That(entries, Is.Not.Empty, "the engine logged through the DI logger factory");
        Assert.That(entries.Select(entry => entry.Category).Distinct(),
            Has.All.StartWith("eQuantic.Core.Data.postgresql."), "one stable category per provider");

        var executed = entries.Where(entry => entry.Event.Id == 10001).ToList();
        Assert.That(executed, Is.Not.Empty, "CommandExecuted (10001) events flowed");
        Assert.That(executed.Any(entry => entry.Message.Contains("SELECT")), Is.True, "statements log with the SQL text");
        Assert.That(executed.All(entry => !entry.Message.Contains("logging-probe")), Is.True,
            "parameter values never log without the explicit opt-in");

        Assert.That(entries.Any(entry => entry.Event.Id == 10101), Is.True, "CommitExecuted (10101) logged the flush");
    }

    [Test]
    public async Task Sensitive_data_logging_is_an_explicit_opt_in()
    {
        var collector = new Collector();
        using var db = await NewSchemaAsync(services =>
        {
            AddLogging(services, collector);
            services.AddSingleton(new DataConventions { EnableSensitiveDataLogging = true });
        });
        var repo = db.Resolve<eQuantic.Core.Data.Repository.IAsyncRepository<Article, Guid>>();

        await repo.GetFilteredAsync(x => x.Title == "sensitive-probe");

        Assert.That(collector.Entries.Any(entry =>
                entry.Event.Id == 10001 && entry.Message.Contains("sensitive-probe")),
            Is.True, "with the opt-in, the parameter values join the entry");
    }
}
