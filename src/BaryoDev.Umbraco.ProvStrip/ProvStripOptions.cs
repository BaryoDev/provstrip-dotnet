using BaryoDev.ProvStrip;

namespace BaryoDev.Umbraco.ProvStrip;

/// <summary>Settings under <c>BaryoDev:ProvStrip</c>.</summary>
public sealed class ProvStripOptions
{
    public const string Section = "BaryoDev:ProvStrip";

    /// <summary>AI and tool provenance: Flag labels it, Auto strips it on upload.</summary>
    public CategoryMode Provenance { get; set; } = CategoryMode.Flag;

    /// <summary>Camera data: Flag labels it, Auto strips it on upload.</summary>
    public CategoryMode Camera { get; set; } = CategoryMode.Flag;

    /// <summary>Keep unknown segments when stripping instead of refusing.</summary>
    public bool KeepUnknown { get; set; }

    /// <summary>Media items checked per run of the scan job.</summary>
    public int ScanBatchSize { get; set; } = 50;

    /// <summary>Files larger than this are not read; they show as needing a look.</summary>
    public long MaxFileBytes { get; set; } = 64L * 1024 * 1024;
}
