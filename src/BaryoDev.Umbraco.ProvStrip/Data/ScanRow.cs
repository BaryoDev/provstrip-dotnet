using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace BaryoDev.Umbraco.ProvStrip.Data;

[TableName(TableName)]
[PrimaryKey(nameof(Id), AutoIncrement = true)]
[ExplicitColumns]
public sealed class ScanRow
{
    public const string TableName = "baryoDevProvStripScan";

    [Column("Id")]
    [PrimaryKeyColumn(AutoIncrement = true)]
    public int Id { get; set; }

    [Column("State")]
    [Length(16)]
    public string State { get; set; } = "";

    [Column("CursorId")]
    public int CursorId { get; set; }

    [Column("Total")]
    public int Total { get; set; }

    [Column("Checked")]
    public int Checked { get; set; }

    [Column("WithFindings")]
    public int WithFindings { get; set; }

    [Column("StartedUtc")]
    [NullSetting(NullSetting = NullSettings.Null)]
    public DateTime? StartedUtc { get; set; }

    [Column("FinishedUtc")]
    [NullSetting(NullSetting = NullSettings.Null)]
    public DateTime? FinishedUtc { get; set; }
}
