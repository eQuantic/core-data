using eQuantic.Core.Data.Tools;

namespace eQuantic.Core.Data.Tests;

/// <summary>
///     The folder <c>eqdata migrations add --output</c> writes into names the generated files' namespace, so it has to
///     read the same on Windows and elsewhere, and always give a namespace the compiler accepts.
/// </summary>
[TestFixture]
public sealed class MigrationFolderTests
{
    [TestCase("Migrations", "Shop.Migrations")]
    [TestCase("Migrations/Space", "Shop.Migrations.Space")]
    [TestCase(@"Migrations\Space", "Shop.Migrations.Space")]
    [TestCase(@"Data\Migrations/Space", "Shop.Data.Migrations.Space")]
    [TestCase("/Migrations/", "Shop.Migrations")]
    [TestCase(@"\Migrations\\Space\", "Shop.Migrations.Space")]
    [TestCase("Migrations//Space", "Shop.Migrations.Space")]
    public void Both_separators_split_the_folder_and_empty_segments_are_dropped(string output, string expected)
    {
        Assert.That(MigrationFolder.Parse(output).Namespace("Shop"), Is.EqualTo(expected));
    }

    [TestCase("2026", "_2026")]
    [TestCase("schema-v2", "schema_v2")]
    [TestCase("my migrations", "my_migrations")]
    [TestCase("v1.2", "v1_2")]
    [TestCase("class", "@class")]
    [TestCase("Événements", "Événements")]
    public void Each_segment_becomes_an_identifier(string segment, string expected)
    {
        Assert.That(MigrationFolder.Parse("Migrations/" + segment).Namespace("Shop"), Is.EqualTo("Shop.Migrations." + expected));
    }

    [Test]
    public void The_folder_on_disk_keeps_its_names_and_stays_inside_the_project()
    {
        var project = Path.Combine(Path.GetTempPath(), "shop");

        Assert.That(MigrationFolder.Parse(@"\Data\schema-v2/").PathUnder(project),
            Is.EqualTo(Path.Combine(project, "Data", "schema-v2")),
            "a backslash separates on every system, and a leading separator does not leave the project");
    }

    [TestCase("")]
    [TestCase("/")]
    [TestCase(@"\\")]
    [TestCase("../Migrations")]
    [TestCase("Migrations/./Space")]
    [TestCase(@"C:\Migrations")]
    public void A_folder_that_names_no_namespace_inside_the_project_is_refused(string output)
    {
        var error = Assert.Throws<ToolException>(() => MigrationFolder.Parse(output));
        Assert.That(error!.Message, Does.Contain("--output").And.Contain("relative to the project"));
    }
}
