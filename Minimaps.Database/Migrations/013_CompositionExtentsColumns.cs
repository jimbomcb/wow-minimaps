using FluentMigrator;

namespace Minimaps.Database.Migrations;

[Migration(13)]
public class CompositionExtentsColumns : Migration
{
    public override void Up()
    {
        // Just have 4 straight columns rather than the JSONB for each row
        Execute.Sql("ALTER TABLE compositions ADD COLUMN x0 SMALLINT, ADD COLUMN y0 SMALLINT, ADD COLUMN x1 SMALLINT, ADD COLUMN y1 SMALLINT;");
        Execute.Sql(@"
            UPDATE compositions SET
                x0 = (extents->>'x0')::SMALLINT,
                y0 = (extents->>'y0')::SMALLINT,
                x1 = (extents->>'x1')::SMALLINT,
                y1 = (extents->>'y1')::SMALLINT
            WHERE extents IS NOT NULL;
        ");
        Execute.Sql("ALTER TABLE compositions DROP COLUMN extents;");
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE compositions ADD COLUMN extents JSONB;");
        Execute.Sql(@"
            UPDATE compositions SET extents = jsonb_build_object('x0', x0, 'y0', y0, 'x1', x1, 'y1', y1)
            WHERE x0 IS NOT NULL;
        ");
        Execute.Sql("ALTER TABLE compositions DROP COLUMN x0, DROP COLUMN y0, DROP COLUMN x1, DROP COLUMN y1;");
    }
}
