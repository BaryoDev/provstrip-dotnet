using BaryoDev.Umbraco.ProvStrip.Data;
using BaryoDev.Umbraco.ProvStrip.Media;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Extensions;

namespace BaryoDev.Umbraco.ProvStrip;

public sealed class ProvStripComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        AddServices(builder.Services);
        builder.AddNotificationAsyncHandler<MediaSavingNotification, MediaSavingHandler>();
    }

    internal static IServiceCollection AddServices(IServiceCollection services)
    {
        services.AddOptions<ProvStripOptions>().BindConfiguration(ProvStripOptions.Section);
        // Stateless over the scope provider, so one instance serves every request and the background job.
        services.AddSingleton<IFindingStore, FindingStore>();
        services.AddSingleton<IScanStore, ScanStore>();
        services.AddSingleton<MediaProvStrip>();
        services.AddSingleton<IMediaProvStrip>(sp => sp.GetRequiredService<MediaProvStrip>());
        return services;
    }
}
