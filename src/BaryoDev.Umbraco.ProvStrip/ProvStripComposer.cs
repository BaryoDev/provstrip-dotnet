using BaryoDev.Umbraco.ProvStrip.Data;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace BaryoDev.Umbraco.ProvStrip;

public sealed class ProvStripComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder) => AddServices(builder.Services);

    internal static IServiceCollection AddServices(IServiceCollection services)
    {
        services.AddOptions<ProvStripOptions>().BindConfiguration(ProvStripOptions.Section);
        // Stateless over the scope provider, so one instance serves every request and the background job.
        services.AddSingleton<IFindingStore, FindingStore>();
        services.AddSingleton<IScanStore, ScanStore>();
        return services;
    }
}
