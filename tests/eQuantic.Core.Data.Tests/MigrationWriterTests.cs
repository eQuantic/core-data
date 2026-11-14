using eQuantic.Core.Data.Evolution;

namespace eQuantic.Core.Data.Tests;

/// <summary>
///     What a generated migration does with the two changes no store operation performs faithfully: an enum switched
///     between its numbers and its labels, and a member whose generation by the store changed. Neither may come out
///     as a step that runs. A type conversion would write the number 1 as the text "1", never as its label, and a
///     store left not assigning what the model stopped writing fails the first insert, or repeats a number. So each
///     comes out as a decision that fails the build and says what the hand-written step must do on that store.
/// </summary>
[TestFixture]
public sealed class MigrationWriterTests
{
    private const string Order = "Shop.Order";
    private static readonly DateTime Stamp = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Providers =
        ["postgresql", "mssql", "mysql", "mariadb", "mongodb", "cosmosdb", "cassandra"];

    /// <summary>The stored type each provider records for an enum stored as its number.</summary>
    private static string NumbersOf(string provider) => provider == "cassandra" ? "int" : "Shop.OrderState";

    private static string Write(string provider, params FieldSnapshot[] change)
    {
        var before = new ModelSnapshot(provider, [new EntitySnapshot(Order, "orders", [change[0]]) { Keys = ["Id"] }]);
        var after = new ModelSnapshot(provider, [new EntitySnapshot(Order, "orders", [change[1]]) { Keys = ["Id"] }]);
        return MigrationWriter.Write(ModelDiffer.Compare(before, after), "Evolve", "Shop.Migrations", Stamp);
    }

    private static IReadOnlyList<string> Decisions(string source) =>
        source.Split('\n').Where(line => line.StartsWith("#error ", StringComparison.Ordinal)).ToList();

    // ---- enums stored as labels --------------------------------------------------------------------

    [TestCaseSource(nameof(Providers))]
    public void A_switch_from_numbers_to_labels_is_handed_back_and_never_converted(string provider)
    {
        var source = Write(provider,
            new FieldSnapshot("State", "state", NumbersOf(provider)),
            new FieldSnapshot("State", "state", "labels:Shop.OrderState"));

        Assert.That(source, Does.Not.Contain(".ConvertField("),
            "a conversion writes the number 1 as the text '1', never as its label");
        Assert.That(source, Does.Not.Contain("migration.For<"), "nothing about this change is a step that can run");
        var decision = Decisions(source).Single();
        Assert.That(decision, Does.Contain("'Shop.Order.State' is now stored as the labels of Shop.OrderState"));
        Assert.That(decision, Does.Contain("rewrites each stored number as its label"));
        Assert.That(decision, Does.Contain("EnumLabels.Of(typeof(Shop.OrderState))"));
    }

    [TestCaseSource(nameof(Providers))]
    public void A_switch_from_labels_to_numbers_is_handed_back_and_never_converted(string provider)
    {
        var source = Write(provider,
            new FieldSnapshot("State", "state", "labels:Shop.OrderState"),
            new FieldSnapshot("State", "state", NumbersOf(provider)));

        Assert.That(source, Does.Not.Contain(".ConvertField("));
        var decision = Decisions(source).Single();
        Assert.That(decision, Does.Contain("is now stored as the numbers of Shop.OrderState"));
        Assert.That(decision, Does.Contain("as its number"));
    }

    [TestCase("postgresql", "CREATE TYPE … AS ENUM")]
    [TestCase("postgresql", "ALTER COLUMN … TYPE … USING")]
    [TestCase("mysql", "read as the position of a label, counted from 1")]
    [TestCase("mariadb", "read as the position of a label, counted from 1")]
    [TestCase("mssql", "adds the CHECK that holds it to the labels")]
    [TestCase("mongodb", "in every document")]
    [TestCase("cosmosdb", "one read and one replace per document")]
    [TestCase("cassandra", "Cassandra cannot change a column's type")]
    public void A_switch_to_labels_says_what_the_step_does_on_that_store(string provider, string expected)
    {
        var source = Write(provider,
            new FieldSnapshot("State", "state", NumbersOf(provider)),
            new FieldSnapshot("State", "state", "labels:Shop.OrderState"));

        Assert.That(Decisions(source).Single(), Does.Contain(expected));
    }

    [Test]
    public void A_collection_switched_to_labels_names_its_elements()
    {
        var source = Write("postgresql",
            new FieldSnapshot("History", "history",
                "System.Collections.Generic.List`1[[Shop.OrderState, Shop, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]]"),
            new FieldSnapshot("History", "history", "labels:Shop.OrderState[]"));

        Assert.That(Decisions(source).Single(), Does.Contain("rewrites each number in the stored arrays as its label"));
    }

    [Test]
    public void A_switch_between_two_enums_labels_says_to_rewrite_one_as_the_other()
    {
        var source = Write("mysql",
            new FieldSnapshot("State", "state", "labels:Shop.OrderState"),
            new FieldSnapshot("State", "state", "labels:Shop.FulfilmentState"));

        var decision = Decisions(source).Single();
        Assert.That(decision, Does.Contain("rewrites each stored label of Shop.OrderState as the label of Shop.FulfilmentState"));
        Assert.That(decision, Does.Contain("new ENUM(…)"));
    }

    [Test]
    public void A_conversion_between_plain_types_is_still_generated()
    {
        var source = Write("postgresql",
            new FieldSnapshot("Total", "total", "System.Int32"),
            new FieldSnapshot("Total", "total", "System.Int64"));

        Assert.That(source, Does.Contain(".ConvertField(x => x.Total, MigrationFieldType.Int32, MigrationFieldType.Int64)"));
        Assert.That(Decisions(source), Is.Empty);
    }

    // ---- store-generated members -------------------------------------------------------------------

    [TestCaseSource(nameof(Providers))]
    public void A_member_that_becomes_a_sequence_is_handed_back_with_where_the_numbers_start(string provider)
    {
        var source = Write(provider,
            new FieldSnapshot("Number", "number", "System.Int64"),
            new FieldSnapshot("Number", "number", "System.Int64") { Generated = "sequence" });

        Assert.That(source, Does.Not.Contain("migration.For<"));
        var decision = Decisions(source).Single();
        Assert.That(decision, Does.Contain("'Shop.Order.Number' is now numbered by a sequence the store keeps"));
        Assert.That(decision, Does.Contain("the largest value already stored"),
            "a sequence that starts below what is stored hands out numbers already taken");
    }

    [TestCase("postgresql", "ADD GENERATED BY DEFAULT AS IDENTITY (START WITH the largest value + 1)")]
    [TestCase("mssql", "DEFAULT (NEXT VALUE FOR it)")]
    [TestCase("mysql", "AUTO_INCREMENT")]
    [TestCase("mariadb", "CREATE SEQUENCE … START WITH the largest value + 1")]
    [TestCase("mongodb", "\"_sequences\" collection")]
    [TestCase("cosmosdb", "\"_sequences\" container")]
    [TestCase("cassandra", "\"equantic_sequences\" table")]
    public void A_sequence_says_what_the_step_does_on_that_store(string provider, string expected)
    {
        var source = Write(provider,
            new FieldSnapshot("Number", "number", "System.Int64"),
            new FieldSnapshot("Number", "number", "System.Int64") { Generated = "sequence" });

        Assert.That(Decisions(source).Single(), Does.Contain(expected));
    }

    [TestCase("postgresql", "DEFAULT now() and NOT NULL")]
    [TestCase("mssql", "DEFAULT SYSUTCDATETIME()")]
    [TestCase("mysql", "DEFAULT (UTC_TIMESTAMP(6)) and NOT NULL")]
    [TestCase("mongodb", "give the ones that must have a time a value with an Update step")]
    [TestCase("cassandra", "toTimestamp(now())")]
    public void A_member_the_clock_stamps_is_handed_back_with_its_default(string provider, string expected)
    {
        var source = Write(provider,
            new FieldSnapshot("At", "at", "System.DateTime"),
            new FieldSnapshot("At", "at", "System.DateTime") { Generated = "clock" });

        var decision = Decisions(source).Single();
        Assert.That(decision, Does.Contain("is now stamped with the store's clock"));
        Assert.That(decision, Does.Contain(expected));
    }

    [Test]
    public void A_member_given_a_sql_default_is_handed_back_with_that_default()
    {
        var source = Write("postgresql",
            new FieldSnapshot("At", "at", "System.DateTime"),
            new FieldSnapshot("At", "at", "System.DateTime") { Generated = "default clock_timestamp()" });

        var decision = Decisions(source).Single();
        Assert.That(decision, Does.Contain("filled by the store with the default clock_timestamp()"));
        Assert.That(decision, Does.Contain("DEFAULT clock_timestamp() and NOT NULL"));
    }

    [TestCase("postgresql", "DROP IDENTITY")]
    [TestCase("mssql", "the sequence it draws from")]
    [TestCase("mysql", "without AUTO_INCREMENT")]
    [TestCase("mongodb", "its counter")]
    public void A_member_that_stops_being_a_sequence_is_handed_back(string provider, string expected)
    {
        var source = Write(provider,
            new FieldSnapshot("Number", "number", "System.Int64") { Generated = "sequence" },
            new FieldSnapshot("Number", "number", "System.Int64"));

        var decision = Decisions(source).Single();
        Assert.That(decision, Does.Contain("is now written by the application (it was numbered by a sequence the store keeps)"));
        Assert.That(decision, Does.Contain(expected));
    }

    [Test]
    public void A_store_the_writer_does_not_know_still_gets_a_decision()
    {
        var source = Write("somestore",
            new FieldSnapshot("Number", "number", "System.Int64"),
            new FieldSnapshot("Number", "number", "System.Int64") { Generated = "sequence" });

        Assert.That(Decisions(source).Single(), Does.Contain("the largest value already stored"));
    }
}
