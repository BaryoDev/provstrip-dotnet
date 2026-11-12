using Umbraco.Cms.Core.Packaging;
using Umbraco.Cms.Infrastructure.Migrations;

namespace BaryoDev.Umbraco.ProvStrip.Data;

public sealed class ProvStripMigrationPlan() : PackageMigrationPlan("BaryoDev.Umbraco.ProvStrip")
{
    protected override void DefinePlan() => To<CreateTables>("6f1c2a9e-3b4d-4e8f-9a0b-1c2d3e4f5a6b");
}

public sealed class CreateTables(IMigrationContext context) : AsyncMigrationBase(context)
{
    protected override Task MigrateAsync()
    {
        if (!TableExists(FindingRow.TableName)) Create.Table<FindingRow>().Do();
        if (!TableExists(ScanRow.TableName)) Create.Table<ScanRow>().Do();
        return Task.CompletedTask;
    }
}
