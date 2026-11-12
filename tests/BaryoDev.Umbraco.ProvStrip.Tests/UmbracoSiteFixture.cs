using BaryoDev.ProvStrip.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Media;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Extensions;

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
        // Each fixture gets its own content root, so Umbraco's TEMP (MainDom lock, Examine indexes), logs and
        // anything it writes back to appsettings stay out of the repository and out of other runs' way.
        builder.UseContentRoot(dataDirectory);
        // Umbraco generates and saves an imaging key when none is set. A random one per run means it never writes.
        builder.UseSetting("Umbraco:CMS:Imaging:HMACSecretKey", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64)));
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

    /// <summary>Creates a media item with this file, the way code or an import would.</summary>
    public Task<IMedia> UploadAsync(byte[] bytes, string fileName, string mediaType = Constants.Conventions.MediaTypes.Image)
    {
        var mediaService = Resolve<IMediaService>();
        var media = mediaService.CreateMedia(Path.GetFileNameWithoutExtension(fileName), Constants.System.Root, mediaType);
        using var stream = new MemoryStream(bytes);
        media.SetValue(Resolve<MediaFileManager>(), Resolve<MediaUrlGeneratorCollection>(), Resolve<IShortStringHelper>(),
            Resolve<IContentTypeBaseServiceProvider>(), Constants.Conventions.Media.File, fileName, stream);
        var saved = mediaService.Save(media);
        if (!saved.Success) throw new InvalidOperationException($"Could not save {fileName}: {saved.Result}");
        return Task.FromResult(media);
    }

    public Task<IMedia> UploadAsync(string fixtureName) => UploadAsync(Fixtures.Read(fixtureName), fixtureName);

    public IMedia Reload(IMedia media) => Resolve<IMediaService>().GetById(media.Key)!;

    public string UrlOf(IMedia media) =>
        media.TryGetMediaPath(Constants.Conventions.Media.File, Resolve<MediaUrlGeneratorCollection>(), out var path)
            ? path! : throw new InvalidOperationException("media has no file");

    public string RelativePathOf(IMedia media) => Resolve<MediaFileManager>().FileSystem.GetRelativePath(UrlOf(media));

    public Task<byte[]> ReadMediaFileAsync(IMedia media)
    {
        using var stream = Resolve<MediaFileManager>().FileSystem.OpenFile(RelativePathOf(media));
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Task.FromResult(copy.ToArray());
    }

    /// <summary>A resize URL Umbraco will honour, signed when an imaging key is set.</summary>
    public string CropUrl(IMedia media, int width) =>
        Resolve<IImageUrlGenerator>().GetImageUrl(new ImageUrlGenerationOptions(UrlOf(media)) { Width = width })!;

    /// <summary>camera.jpg with c2pa.jpg's APP11 (C2PA) segments inserted after SOI: provenance and camera data in one file.</summary>
    public static byte[] MixedJpeg()
    {
        var c2pa = Fixtures.Read("c2pa.jpg");
        var app11 = new List<byte>();
        for (var i = 2; i + 4 <= c2pa.Length && c2pa[i] == 0xFF && c2pa[i + 1] != 0xDA;)
        {
            var length = (c2pa[i + 2] << 8) | c2pa[i + 3];
            if (c2pa[i + 1] == 0xEB) app11.AddRange(c2pa[i..(i + 2 + length)]);
            i += 2 + length;
        }
        var camera = Fixtures.Read("camera.jpg");
        return [.. camera[..2], .. app11, .. camera[2..]];
    }

    /// <summary>A minimal PDF, for a non-image upload.</summary>
    public static byte[] Pdf() => "%PDF-1.4\n1 0 obj <<>> endobj\ntrailer <<>>\n%%EOF\n"u8.ToArray();

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
