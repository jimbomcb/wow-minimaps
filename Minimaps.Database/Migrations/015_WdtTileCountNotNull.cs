using FluentMigrator;

namespace Minimaps.Database.Migrations;

[Migration(15)]
public class WdtTileCountNotNull : Migration
{
    public override void Up()
    {
        Execute.Sql("UPDATE build_maps SET wdt_tile_count = 0 WHERE wdt_tile_count IS NULL;");
        Execute.Sql("ALTER TABLE build_maps ALTER COLUMN wdt_tile_count SET NOT NULL, ALTER COLUMN wdt_tile_count SET DEFAULT 0;");
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE build_maps ALTER COLUMN wdt_tile_count DROP NOT NULL, ALTER COLUMN wdt_tile_count DROP DEFAULT;");
    }
}
