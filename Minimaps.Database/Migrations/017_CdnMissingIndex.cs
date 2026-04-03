using FluentMigrator;

namespace Minimaps.Database.Migrations;

[Migration(17)]
public class CdnMissingIndex : Migration
{
    public override void Up()
    {
        // Partial GIN index for the cdn_missing sweep query (runs every build scan).
        // GIN indexes individual array elements, only the small subset of non-null cdn_missing get indexed.
        Execute.Sql(@"
            CREATE INDEX IX_build_map_layers_cdn_missing
            ON build_map_layers USING GIN (cdn_missing)
            WHERE cdn_missing IS NOT NULL;
        ");
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS IX_build_map_layers_cdn_missing;");
    }
}
