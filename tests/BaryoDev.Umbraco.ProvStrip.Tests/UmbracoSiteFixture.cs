using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BaryoDev.Umbraco.ProvStrip.Tests;

/// <summary>
/// Boots the test site once per collection against a throwaway SQLite database, with an unattended install.
/// A real boot is the point: it is what proves the composer's registrations resolve and the migration runs.
/// </summary>
public class UmbracoSiteFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    readonly string dataDirectory = Path.Combine(Path.GetTempPath(), $"provstrip-umbraco-{Guid.NewGuid():N}");

    /// <summary>Extra settings for a fixture that needs them, applied over appsettings.json.</summary>
    protected virtual IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(dataDirectory);
        var db = Path.Combine(dataDirectory, "Umbraco.sqlite.db");
        builder.UseSetting("ConnectionStrings:umbracoDbDSN", $"Data Source={db};Cache=Shared;Foreign Keys=True;Pooling=True");
        builder.UseSetting("ConnectionStrings:umbracoDbDSN_ProviderName", "Microsoft.Data.Sqlite");
        builder.UseSetting("Umbraco:CMS:Unattended:InstallUnattended", "true");
        builder.UseSetting("Umbraco:CMS:Unattended:UnattendedUserName", "Test Admin");
        builder.UseSetting("Umbraco:CMS:Unattended:UnattendedUserEmail", "test@example.com");
        builder.UseSetting("Umbraco:CMS:Unattended:UnattendedUserPassword", "LocalOnly-ChangeMe-1234!");
        // Media goes to the temp folder too, so a run never leaves files in the test site.
        builder.UseSetting("Umbraco:CMS:Global:UmbracoMediaPhysicalRootPath", Path.Combine(dataDirectory, "media"));
        foreach (var (key, value) in Settings) builder.UseSetting(key, value);
        builder.UseEnvironment("Development");
    }

    public HttpClient AnonymousClient() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public T Resolve<T>() where T : notnull => Services.GetRequiredService<T>();

    public async Task InitializeAsync()
    {
        // Builds the host and lets the unattended install finish before any test asks for a service.
        using var response = await AnonymousClient().GetAsync("/");
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(dataDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

[CollectionDefinition(Name)]
public class UmbracoCollection : ICollectionFixture<UmbracoSiteFixture>
{
    public const string Name = "umbraco";
}
