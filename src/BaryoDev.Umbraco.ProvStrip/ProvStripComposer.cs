using BaryoDev.Umbraco.ProvStrip.Data;
using BaryoDev.Umbraco.ProvStrip.Media;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
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
        // A site's build lists referenced MVC assemblies for it, but not always this one (seen with a project
        // reference). Adding it here maps the Management API controller either way, and only once.
        builder.Services.AddMvcCore().ConfigureApplicationPartManager(manager =>
        {
            var assembly = typeof(ProvStripComposer).Assembly;
            if (!manager.ApplicationParts.OfType<AssemblyPart>().Any(p => p.Assembly == assembly))
                manager.ApplicationParts.Add(new AssemblyPart(assembly));
        });
    }

    internal static IServiceCollection AddServices(IServiceCollection services)
    {
        services.AddOptions<ProvStripOptions>().BindConfiguration(ProvStripOptions.Section);
        // Stateless over the scope provider, so one instance serves every request and the background job.
        services.AddSingleton<IFindingStore, FindingStore>();
        services.AddSingleton<IScanStore, ScanStore>();
        services.AddSingleton<MediaProvStrip>();
        services.AddSingleton<IMediaProvStrip>(sp => sp.GetRequiredService<MediaProvStrip>());
        services.AddSingleton<Api.RemoveAllQueue>();
        services.AddSingleton<Api.RemoveAllWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<Api.RemoveAllWorker>());
        return services;
    }
}
