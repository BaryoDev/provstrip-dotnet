using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace BaryoDev.Umbraco.ProvStrip.Data;

[TableName(TableName)]
[PrimaryKey(nameof(Id), AutoIncrement = true)]
[ExplicitColumns]
public sealed class FindingRow
{
    public const string TableName = "baryoDevProvStripFinding";

    [Column("Id")]
    [PrimaryKeyColumn(AutoIncrement = true)]
    public int Id { get; set; }

    [Column("MediaKey")]
    [Index(IndexTypes.NonClustered, Name = "IX_baryoDevProvStripFinding_MediaKey")]
    public Guid MediaKey { get; set; }

    [Column("ContentHash")]
    [Length(64)]
    [Index(IndexTypes.NonClustered, Name = "IX_baryoDevProvStripFinding_Hash", ForColumns = "ContentHash,Category")]
    public string ContentHash { get; set; } = "";

    [Column("Category")]
    [Length(16)]
    public string Category { get; set; } = "";

    [Column("RuleId")]
    [Length(64)]
    public string RuleId { get; set; } = "";

    [Column("Description")]
    [Length(255)]
    public string Description { get; set; } = "";

    [Column("Status")]
    [Length(16)]
    public string Status { get; set; } = "";

    [Column("UpdatedUtc")]
    public DateTime UpdatedUtc { get; set; }
}
