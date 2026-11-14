using BaryoDev.Umbraco.ProvStrip.Data;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace BaryoDev.Umbraco.ProvStrip.Media;

/// <summary>Drops the findings of media that is deleted or moved to the recycle bin, so scan results do not list it.</summary>
internal sealed class MediaRemovedHandler(IFindingStore store)
    : INotificationAsyncHandler<MediaDeletedNotification>, INotificationAsyncHandler<MediaMovedToRecycleBinNotification>
{
    public async Task HandleAsync(MediaDeletedNotification notification, CancellationToken cancellationToken)
    {
        foreach (var media in notification.DeletedEntities) await store.DeleteForMediaAsync(media.Key);
    }

    public async Task HandleAsync(MediaMovedToRecycleBinNotification notification, CancellationToken cancellationToken)
    {
        foreach (var move in notification.MoveInfoCollection) await store.DeleteForMediaAsync(move.Entity.Key);
    }
}
