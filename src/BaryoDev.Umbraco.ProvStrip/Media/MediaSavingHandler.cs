using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace BaryoDev.Umbraco.ProvStrip.Media;

/// <summary>
/// Checks every media item saved with a new file: backoffice and Management API uploads, imports and code.
/// Categories set to Auto are stripped from the stored file before the save completes. A failure here is
/// logged and never blocks the upload.
/// </summary>
internal sealed class MediaSavingHandler(MediaProvStrip strip, ILogger<MediaSavingHandler> logger)
    : INotificationAsyncHandler<MediaSavingNotification>
{
    public async Task HandleAsync(MediaSavingNotification notification, CancellationToken cancellationToken)
    {
        if (ReentryGuard.IsActive) return;
        foreach (var media in notification.SavedEntities)
        {
            if (!media.IsPropertyDirty(Constants.Conventions.Media.File)) continue;
            try
            {
                await strip.CheckUploadAsync(media, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "ProvStrip could not check media {MediaKey} on save", media.Key);
            }
        }
    }
}
