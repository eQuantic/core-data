using eQuantic.Core.Data.Migration;
using eQuantic.Core.Data.Relational;
using eQuantic.Core.Data.Relational.Migration;
using eQuantic.Core.Data.Repository;
using eQuantic.Core.Domain.Entities;

namespace eQuantic.Core.Data.PostgreSql.Tests;

/// <summary>An order entity exercising the type surface: strings, nullable strings, decimals, arrays, UTC timestamps and navigations.</summary>
public sealed class SaleOrder : IEntity<Guid>
{
    public Guid Id { get; set; }

    public string Customer { get; set; } = "";

    public string? Status { get; set; }

    public decimal Total { get; set; }

    public int Quantity { get; set; }

    public List<string> Tags { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    public Guid BuyerId { get; set; }

    public Dictionary<string, string> Attributes { get; set; } = [];

    public Buyer? Buyer { get; set; }

    public List<OrderItem> Items { get; set; } = [];

    public List<Invoice> Invoices { get; set; } = [];

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A referenced entity for the reference-include shape (<c>SaleOrder.Buyer</c> via <c>BuyerId</c>).</summary>
public sealed class Buyer : IEntity<Guid>
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>An element entity for the collection-include shape (<c>SaleOrder.Items</c> via <c>SaleOrderId</c>).</summary>
public sealed class OrderItem : IEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid SaleOrderId { get; set; }

    public string Product { get; set; } = "";

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>
///     A full-lifecycle entity: the <c>eQuantic.Core.Domain</c> interfaces bring stamped <c>CreatedAt</c>/
///     <c>UpdatedAt</c>, soft deletes with the automatic live-rows filter, and <c>Version</c> is the
///     optimistic-concurrency token.
/// </summary>
public sealed class Document : IEntity<Guid>, IEntityTimeMark, IEntityTimeTrack, IEntityTimeEnded
{
    public Guid Id { get; set; }

    public string Title { get; set; } = "";

    public int Version { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>
///     An entity built on <c>eQuantic.Core.DataModel</c>'s richest base: Id + the full who/when audit
///     (<c>CreatedAt/ById</c>, <c>UpdatedAt/ById</c>, <c>DeletedAt/ById</c>) — everything by convention.
/// </summary>
public sealed class AuditedTicket : eQuantic.Core.DataModel.EntityHistoryDataBase
{
    public string Label { get; set; } = "";
}

/// <summary>An entity modeled <b>entirely by annotations</b> — the store-neutral eQuantic vocabulary, no fluent.</summary>
[eQuantic.Core.Data.Modeling.Entity("annotated_orders")]
public sealed class AnnotatedOrder : IEntity<Guid>
{
    [eQuantic.Core.Data.Modeling.EntityKey]
    public Guid Code { get; set; }

    [eQuantic.Core.Data.Modeling.StoredAs("client_name")]
    public string Name { get; set; } = "";

    [eQuantic.Core.Data.Modeling.ConcurrencyToken]
    public int Revision { get; set; }

    [eQuantic.Core.Data.Modeling.Unmapped]
    public string Scratch { get; set; } = "";

    public Guid GetKey() => Code;

    public void SetKey(Guid key) => Code = key;
}

/// <summary>An entity whose foreign keys do <b>not</b> follow the conventions — the model declares them.</summary>
public sealed class Invoice : IEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid OrderCode { get; set; }

    public decimal Amount { get; set; }

    public SaleOrder? Order { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>A DDD value object: self-validating, immutable, stored as text through a value converter.</summary>
public sealed record EmailAddress
{
    private EmailAddress(string value) => Value = value;

    public string Value { get; }

    public static EmailAddress Create(string value) => new(value.Trim().ToLowerInvariant());
}

public enum SubscriberStatus
{
    Pending,
    Active,
    Cancelled,
}

/// <summary>An entity whose domain types cross into columns only through converters.</summary>
public sealed class Subscriber : IEntity<Guid>
{
    public Guid Id { get; set; }

    public EmailAddress Email { get; set; } = EmailAddress.Create("nobody@nowhere");

    public SubscriberStatus Status { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>An entity whose table starts <b>bare</b> (created by a Run step): AddField/DropField evolve it.</summary>
public sealed class LegacyNote : IEntity<Guid>
{
    public Guid Id { get; set; }

    public string? Text { get; set; }

    public int Stars { get; set; }

    public Guid GetKey() => Id;

    public void SetKey(Guid key) => Id = key;
}

/// <summary>An identity-keyed entity: the database generates the key and the commit reads it back.</summary>
public sealed class Ticket : IEntity<long>
{
    public long Id { get; set; }

    public string Label { get; set; } = "";

    public long GetKey() => Id;

    public void SetKey(long key) => Id = key;
}

/// <summary>The relational mapping shared by the integration tests.</summary>
internal static class TestSchema
{
    public static void Configure(RelationalModelBuilder builder) => builder
        .Entity<SaleOrder>(entity => entity
            .Table("sale_orders")
            .Collection(x => x.Invoices, invoice => invoice.OrderCode))
        .Entity<Buyer>(_ => { })
        .Entity<OrderItem>(_ => { })
        .Entity<Invoice>(entity => entity
            .Reference(x => x.Order, x => x.OrderCode))
        .Entity<LegacyNote>(entity => entity.Table("legacy_notes"))
        .Entity<Document>(entity => entity.ConcurrencyToken(x => x.Version))
        .Entity<Subscriber>(entity => entity
            .Converts(x => x.Email, email => email.Value, EmailAddress.Create)
            .Converts(x => x.Status, status => status.ToString(), value => Enum.Parse<SubscriberStatus>(value)))
        .Entity<AnnotatedOrder>(_ => { })
        .Entity<AuditedTicket>(entity => entity.Key(x => x.Id, generated: true))
        .Entity<Ticket>(entity => entity.Key(x => x.Id, generated: true));
}

/// <summary>The schema migration the runner discovers: both tables plus a customer index.</summary>
[Migration("PostgreSQL schema setup", 2026, 1, 1, 0, 0, 0)]
public sealed class SchemaSetupMigration : Data.Migration.Migration
{
    /// <inheritdoc />
    public override void Up(IMigrationBuilder migration) => migration
        .For<SaleOrder>(order => order
            .EnsureCollection()
            .Index(x => x.Customer))
        .For<Buyer>(buyer => buyer
            .EnsureCollection())
        .For<OrderItem>(item => item
            .EnsureCollection())
        .For<Document>(document => document
            .EnsureCollection())
        .For<Subscriber>(subscriber => subscriber
            .EnsureCollection())
        .For<Invoice>(invoice => invoice
            .EnsureCollection())
        .For<AnnotatedOrder>(annotated => annotated
            .EnsureCollection())
        .For<AuditedTicket>(audited => audited
            .EnsureCollection())
        .For<Ticket>(ticket => ticket
            .EnsureCollection());
}

/// <summary>
///     The evolution migration: a Run step leaves <c>legacy_notes</c> bare (key plus an obsolete column, the way a
///     live table looks), then <c>AddField</c>/<c>DropField</c> evolve it, and the rich index options build a GIN
///     index over the jsonb column and a filtered index with an inlined-literal predicate.
/// </summary>
[Migration("PostgreSQL schema evolution", 2026, 1, 1, 0, 0, 1)]
public sealed class SchemaEvolutionMigration : Data.Migration.Migration
{
    /// <inheritdoc />
    public override void Up(IMigrationBuilder migration) => migration
        .Run(async (context, cancellationToken) =>
        {
            var command = context.AsRelational().Connection.CreateCommand();
            await using (command)
            {
                command.CommandText = "CREATE TABLE IF NOT EXISTS legacy_notes (id uuid PRIMARY KEY, obsolete text)";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        })
        .For<LegacyNote>(note => note
            .AddField(x => x.Text)
            .AddField(x => x.Stars)
            .DropField("obsolete"))
        .For<SaleOrder>(order => order
            .Index(x => x.Attributes, options => options.Gin().Named("ix_sale_orders_attributes_gin"))
            .Index(x => x.Status, options => options
                .Filtered(x => x.Status != null && x.Total > 0m)
                .Named("ix_sale_orders_status_present")));
}
