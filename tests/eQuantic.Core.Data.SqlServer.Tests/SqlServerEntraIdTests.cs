using Microsoft.Data.SqlClient;

namespace eQuantic.Core.Data.SqlServer.Tests;

/// <summary>
///     SqlClient 7 moved Entra ID authentication into Microsoft.Data.SqlClient.Extensions.Azure. A connection string
///     that signed in with <c>Authentication=Active Directory ...</c> on core-data 7.0 has to keep working on a project
///     that references only this provider, so the provider brings that package, and the driver finds a provider for
///     every Entra ID method. Needs no server: the provider is resolved before any connection is attempted.
/// </summary>
[TestFixture]
public sealed class SqlServerEntraIdTests
{
    [TestCase(SqlAuthenticationMethod.ActiveDirectoryDefault)]
    [TestCase(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity)]
    [TestCase(SqlAuthenticationMethod.ActiveDirectoryServicePrincipal)]
    [TestCase(SqlAuthenticationMethod.ActiveDirectoryInteractive)]
    [TestCase(SqlAuthenticationMethod.ActiveDirectoryWorkloadIdentity)]
    public void Every_Entra_ID_method_has_a_provider(SqlAuthenticationMethod method) =>
        Assert.That(SqlAuthenticationProvider.GetProvider(method), Is.Not.Null,
            $"{method} needs Microsoft.Data.SqlClient.Extensions.Azure, which the provider references");

    [Test]
    public void A_connection_string_with_Entra_ID_is_accepted()
    {
        var builder = new SqlConnectionStringBuilder(
            "Server=tcp:example.database.windows.net,1433;Database=app;Authentication=Active Directory Default;Encrypt=True");

        Assert.That(builder.Authentication, Is.EqualTo(SqlAuthenticationMethod.ActiveDirectoryDefault));
        Assert.That(() => new SqlConnection(builder.ConnectionString).Dispose(), Throws.Nothing);
    }
}
