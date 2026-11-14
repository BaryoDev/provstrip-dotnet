using System.Net;
using BaryoDev.ProvStrip;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.ProvStrip.Tests;

[Collection(UmbracoCollection.Name)]
public class ComposerTests(UmbracoSiteFixture site)
{
    [Fact] public void OptionsDefaultToFlag()
    {
        var o = site.Resolve<IOptions<ProvStripOptions>>().Value;
        (o.Provenance, o.Camera, o.KeepUnknown, o.ScanBatchSize, o.MaxFileBytes)
            .ShouldBe((CategoryMode.Flag, CategoryMode.Flag, false, 50, 64L * 1024 * 1024));
    }

    [Fact] public void OptionsBindFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["BaryoDev:ProvStrip:Camera"] = "Auto", ["BaryoDev:ProvStrip:ScanBatchSize"] = "7" })
            .Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        ProvStripComposer.AddServices(services);
        var o = services.BuildServiceProvider().GetRequiredService<IOptions<ProvStripOptions>>().Value;
        (o.Camera, o.Provenance, o.ScanBatchSize).ShouldBe((CategoryMode.Auto, CategoryMode.Flag, 7));
    }

    [Fact] public void ScanJobIsScheduled()
    {
        var services = new ServiceCollection().AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        ProvStripComposer.AddServices(services);
        services.ShouldContain(d => d.ServiceType == typeof(global::Umbraco.Cms.Infrastructure.BackgroundJobs.IRecurringBackgroundJob)
            && d.ImplementationType == typeof(Scan.ProvStripScanJob));
    }

    [Fact] public async Task SiteBootsWithThePackage()
    {
        using var response = await site.AnonymousClient().GetAsync("/");
        response.StatusCode.ShouldNotBe(HttpStatusCode.InternalServerError);
    }
}
