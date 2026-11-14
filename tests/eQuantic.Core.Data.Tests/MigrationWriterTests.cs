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

    // ---- labels of an enum stored as labels --------------------------------------------------------

    private static readonly LabelSnapshot[] Stages =
        [new("Queued", 0, "queued"), new("InProgress", 1, "in_progress"), new("Done", 2, "done")];

    private static FieldSnapshot Labelled(string member, params LabelSnapshot[] labels) =>
        new(member, member.ToLowerInvariant(), "labels:Shop.OrderState") { Labels = labels };

    private static string Relabel(string provider, LabelSnapshot[] before, LabelSnapshot[] after) =>
        Write(provider, Labelled("State", before), Labelled("State", after));

    [Test]
    public void A_label_added_on_PostgreSQL_is_added_to_the_type_where_it_belongs()
    {
        var source = Relabel("postgresql", Stages,
            [Stages[0], new("Paused", 3, "paused"), Stages[1], Stages[2], new("Failed", 4, "failed")]);

        var decision = Decisions(source).Single();
        Assert.That(decision, Does.Contain("'paused', 'failed' added"));
        Assert.That(decision, Does.Contain("ALTER TYPE … ADD VALUE 'paused' AFTER 'queued'"));
        Assert.That(decision, Does.Contain("ALTER TYPE … ADD VALUE 'failed' AFTER 'done'"));
        Assert.That(decision, Does.Not.Contain("new type"));
    }

    [Test]
    public void A_label_added_first_on_PostgreSQL_goes_before_the_first_one_there()
    {
        var source = Relabel("postgresql", Stages, [new("Draft", 3, "draft"), .. Stages]);

        Assert.That(Decisions(source).Single(), Does.Contain("ALTER TYPE … ADD VALUE 'draft' BEFORE 'queued'"));
    }

    [Test]
    public void A_label_renamed_on_PostgreSQL_is_renamed_in_the_type_and_in_the_rows()
    {
        var source = Relabel("postgresql", Stages, [Stages[0], new("InProgress", 1, "running"), Stages[2]]);

        var decision = Decisions(source).Single();
        Assert.That(decision, Does.Contain("'in_progress' as 'running' renamed"));
        Assert.That(decision, Does.Contain("ALTER TYPE … RENAME VALUE 'in_progress' TO 'running'"));
    }

    [Test]
    public void A_member_renamed_in_code_with_its_number_kept_renames_its_label()
    {
        var source = Relabel("postgresql", Stages, [Stages[0], new("Running", 1, "Running"), Stages[2]]);

        Assert.That(Decisions(source).Single(), Does.Contain("RENAME VALUE 'in_progress' TO 'Running'"),
            "matched by its number once its name no longer matches");
    }

    [Test]
    public void A_label_inserted_with_the_numbers_after_it_shifted_is_an_addition_not_renames()
    {
        var source = Relabel("postgresql", Stages,
            [Stages[0], new("Paused", 1, "paused"), new("InProgress", 2, "in_progress"), new("Done", 3, "done")]);

        var decision = Decisions(source).Single();
        Assert.That(decision, Does.Contain("'paused' added"));
        Assert.That(decision, Does.Not.Contain("RENAME"), "the members are matched by name before their numbers");
    }

    [Test]
    public void A_label_removed_on_PostgreSQL_needs_a_new_type()
    {
        var source = Relabel("postgresql", Stages, [Stages[0], Stages[2]]);

        var decision = Decisions(source).Single();
        Assert.That(decision, Does.Contain("'in_progress' removed"));
        Assert.That(decision, Does.Contain("PostgreSQL cannot remove or reorder an enum type's labels"));
        Assert.That(decision, Does.Contain("rewrites the rows holding 'in_progress' to a label that remains"));
    }

    [Test]
    public void A_label_renamed_and_another_removed_on_PostgreSQL_maps_the_rename_on_the_way_to_the_new_type()
    {
        var source = Relabel("postgresql", Stages, [new("Queued", 0, "waiting"), Stages[2]]);

        var decision = Decisions(source).Single();
        Assert.That(decision, Does.Contain("USING its text (mapping 'queued' as 'waiting')"));
        Assert.That(decision, Does.Not.Contain("rewrites 'queued' as 'waiting'"),
            "the old type has no 'waiting' to rewrite a row to");
    }

    [Test]
    public void Two_columns_of_one_enum_type_on_PostgreSQL_get_one_decision()
    {
        var after = Stages.Append(new LabelSnapshot("Failed", 3, "failed")).ToArray();
        var before = new ModelSnapshot("postgresql",
            [new EntitySnapshot(Order, "orders", [Labelled("Previous", Stages), Labelled("State", Stages)]) { Keys = ["Id"] }]);
        var now = new ModelSnapshot("postgresql",
            [new EntitySnapshot(Order, "orders", [Labelled("Previous", after), Labelled("State", after)]) { Keys = ["Id"] }]);

        var source = MigrationWriter.Write(ModelDiffer.Compare(before, now), "Evolve", "Shop.Migrations", Stamp);

        Assert.That(Decisions(source), Has.Count.EqualTo(1), "the type is one object, whatever uses it");
    }

    [TestCase("mysql")]
    [TestCase("mariadb")]
    public void A_label_added_on_MySQL_restates_the_whole_list(string provider)
    {
        var source = Relabel(provider, Stages, [Stages[0], new("Paused", 3, "paused"), Stages[1], Stages[2]]);

        Assert.That(Decisions(source).Single(),
            Does.Contain("ALTER TABLE … MODIFY COLUMN … ENUM('queued', 'paused', 'in_progress', 'done'), the full list"));
    }

    [Test]
    public void A_label_renamed_on_MySQL_goes_through_a_list_holding_both()
    {
        var source = Relabel("mysql", Stages, [Stages[0], new("InProgress", 1, "running"), Stages[2]]);

        var decision = Decisions(source).Single();
        Assert.That(decision, Does.Contain("first widens the column to an ENUM holding both the old and the new labels"));
        Assert.That(decision, Does.Contain("ENUM('queued', 'running', 'done')"));
    }

    [Test]
    public void A_label_added_on_SQL_Server_replaces_the_check_and_widens_the_column()
    {
        var source = Relabel("mssql", Stages, [.. Stages, new("Cancelled", 3, "cancelled_by_user")]);

        var decision = Decisions(source).Single();
        Assert.That(decision, Does.Contain("drops the CHECK holding the column to the labels"));
        Assert.That(decision, Does.Contain("widens the column to nvarchar(17)"));
        Assert.That(decision, Does.Contain("CHECK ([…] IN (N'queued', N'in_progress', N'done', N'cancelled_by_user'))"));
    }

    [Test]
    public void Labels_moved_on_SQL_Server_need_nothing()
    {
        var source = Relabel("mssql", Stages, [Stages[2], Stages[0], Stages[1]]);

        Assert.That(Decisions(source), Is.Empty, "a CHECK on a list of labels does not care for their order");
    }

    [TestCase("mongodb")]
    [TestCase("cosmosdb")]
    [TestCase("cassandra")]
    public void A_label_added_where_no_enum_type_holds_them_needs_nothing(string provider)
    {
        var source = Relabel(provider, Stages, [.. Stages, new("Failed", 3, "failed")]);

        Assert.That(Decisions(source), Is.Empty, "a string field takes a new label as it is");
    }

    [TestCase("mongodb", "rewrites 'in_progress' as 'running' in every document")]
    [TestCase("cosmosdb", "one read and one replace per document")]
    [TestCase("cassandra", "rewrites 'in_progress' as 'running' in every row")]
    public void A_label_renamed_where_no_enum_type_holds_them_is_rewritten_in_the_data(string provider, string expected)
    {
        var source = Relabel(provider, Stages, [Stages[0], new("InProgress", 1, "running"), Stages[2]]);

        var decision = Decisions(source).Single();
        Assert.That(decision, Does.Contain(expected));
        Assert.That(decision, Does.Contain("the labels' reader refuses a label the enum no longer has"));
    }

    [Test]
    public void A_label_removed_on_MongoDB_is_rewritten_in_the_documents_holding_it()
    {
        var source = Relabel("mongodb", Stages, [Stages[0], Stages[2]]);

        Assert.That(Decisions(source).Single(),
            Does.Contain("rewrites every document holding 'in_progress' to a label that remains"));
    }

    // ---- a key the store numbers -------------------------------------------------------------------

    private static string Rekey(string provider, bool before, bool after)
    {
        var was = new ModelSnapshot(provider, [new EntitySnapshot(Order, "orders", [new FieldSnapshot("Id", "id", "System.Int64")])
        {
            Keys = ["Id"],
            KeyIsGenerated = before,
        }]);
        var now = new ModelSnapshot(provider, [new EntitySnapshot(Order, "orders", [new FieldSnapshot("Id", "id", "System.Int64")])
        {
            Keys = ["Id"],
            KeyIsGenerated = after,
        }]);
        return MigrationWriter.Write(ModelDiffer.Compare(was, now), "Evolve", "Shop.Migrations", Stamp);
    }

    [TestCase("postgresql", "ADD GENERATED BY DEFAULT AS IDENTITY (START WITH the largest key + 1)")]
    [TestCase("mssql", "SQL Server cannot make an existing column an IDENTITY")]
    [TestCase("mysql", "modifies the key column to AUTO_INCREMENT")]
    [TestCase("mariadb", "modifies the key column to AUTO_INCREMENT")]
    [TestCase("mongodb", "starting above the largest key already stored")]
    public void A_key_the_store_starts_numbering_is_handed_back(string provider, string expected)
    {
        var decision = Decisions(Rekey(provider, before: false, after: true)).Single();

        Assert.That(decision, Does.Contain("'Shop.Order' now has its key (Id) numbered by the store"));
        Assert.That(decision, Does.Contain(expected));
    }

    [TestCase("postgresql", "DROP IDENTITY")]
    [TestCase("mssql", "SQL Server cannot take IDENTITY off a column")]
    [TestCase("mysql", "without AUTO_INCREMENT")]
    public void A_key_the_store_stops_numbering_is_handed_back(string provider, string expected)
    {
        var decision = Decisions(Rekey(provider, before: true, after: false)).Single();

        Assert.That(decision, Does.Contain("now has its key (Id) given by the application"));
        Assert.That(decision, Does.Contain(expected));
    }

    // ---- a member added as store-generated ---------------------------------------------------------

    private static string AddGenerated(string provider, string generated) =>
        MigrationWriter.Write(ModelDiffer.Compare(
            new ModelSnapshot(provider, [new EntitySnapshot(Order, "orders", [new FieldSnapshot("Id", "id", "System.Guid")]) { Keys = ["Id"] }]),
            new ModelSnapshot(provider, [new EntitySnapshot(Order, "orders",
            [
                new FieldSnapshot("Id", "id", "System.Guid"),
                new FieldSnapshot("Number", "number", "System.Int64") { Generated = generated },
            ]) { Keys = ["Id"] }])), "Evolve", "Shop.Migrations", Stamp);

    [TestCase("postgresql", "sequence")]
    [TestCase("postgresql", "clock")]
    [TestCase("postgresql", "default now()")]
    [TestCase("mssql", "sequence")]
    [TestCase("mysql", "sequence")]
    [TestCase("mariadb", "sequence")]
    [TestCase("mariadb", "clock")]
    public void A_member_added_as_store_generated_on_a_relational_store_is_left_to_the_store(string provider, string generated)
    {
        var source = AddGenerated(provider, generated);

        Assert.That(source, Does.Contain(".AddField(x => x.Number)"));
        Assert.That(source, Does.Not.Contain(".Update("), "the column the store fills is never written by the migration");
        Assert.That(Decisions(source), Is.Empty, "the engine fills every row already in the table");
    }

    [TestCase("mongodb", "\"_sequences\" collection")]
    [TestCase("cosmosdb", "\"_sequences\" container")]
    [TestCase("cassandra", "\"equantic_sequences\" table")]
    public void A_member_added_as_a_sequence_where_the_records_are_not_filled_is_handed_back(string provider, string counter)
    {
        var source = AddGenerated(provider, "sequence");

        Assert.That(source, Does.Contain(".AddField(x => x.Number)"));
        var decision = Decisions(source).Single();
        Assert.That(decision, Does.Contain("which numbers new inserts only"));
        Assert.That(decision, Does.Contain("gives each of them a number of its own"));
        Assert.That(decision, Does.Contain(counter));
        Assert.That(decision, Does.Contain("to the largest of them"));
    }

    [Test]
    public void A_member_added_as_the_clock_where_the_records_are_not_filled_is_handed_back()
    {
        var decision = Decisions(AddGenerated("cosmosdb", "clock")).Single();

        Assert.That(decision, Does.Contain("is added stamped with the store's clock, which reaches new inserts only"));
        Assert.That(decision, Does.Contain("Update step"));
    }

    // ---- search indexes ----------------------------------------------------------------------------

    private static string Search(string provider, string? from, string? to)
    {
        EntitySnapshot Entity(string? mode) =>
            new(Order, "orders", [new FieldSnapshot("Id", "id", "System.Guid"), new FieldSnapshot("Name", "name", "text")])
            {
                Keys = ["Id"],
                Search = mode is null ? [] : [new SearchSnapshot("Name", mode)],
            };

        return MigrationWriter.Write(ModelDiffer.Compare(new ModelSnapshot(provider, [Entity(from)]), new ModelSnapshot(provider, [Entity(to)])),
            "Evolve", "Shop.Migrations", Stamp);
    }

    [TestCase("Contains", "Contains, case-insensitive")]
    [TestCase("Contains, case-insensitive", "Contains")]
    [TestCase("Prefix", "Contains")]
    public void A_cassandra_search_index_that_matches_another_way_must_be_rebuilt(string from, string to)
    {
        var decision = Decisions(Search("cassandra", from, to)).Single();

        Assert.That(decision, Does.Contain($"changes from {from} to {to}"));
        Assert.That(decision, Does.Contain("DROP INDEX IF EXISTS orders_name_search"));
        Assert.That(decision, Does.Contain(".EnsureCollection()"));
        Assert.That(decision, Does.Contain("must be rebuilt"));
    }

    [Test]
    public void A_cassandra_search_index_declared_or_dropped_says_how()
    {
        Assert.That(Decisions(Search("cassandra", null, "Contains")).Single(), Does.Contain(".EnsureCollection()"));
        Assert.That(Decisions(Search("cassandra", "Contains", null)).Single(), Does.Contain("DROP INDEX IF EXISTS orders_name_search"));
    }

    [Test]
    public void A_postgresql_search_index_serves_every_mode_and_needs_no_rebuild()
    {
        Assert.That(Decisions(Search("postgresql", "Prefix", "Contains")), Is.Empty, "the trigram index serves both");
        Assert.That(Decisions(Search("postgresql", null, "Contains")).Single(), Does.Contain(".EnsureCollection()"));
    }
}
