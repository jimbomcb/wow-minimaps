using FluentMigrator;

namespace Minimaps.Database.Migrations;

[Migration(16)]
public class LayerHashCleanup : Migration
{
    public override void Up()
    {
        // drop the XOR constraint layer type implies which column is populated
        Execute.Sql("ALTER TABLE build_map_layers DROP CONSTRAINT \"ck_build_map_layers_hash\";");

        // Bitmask of which layer types have ever been seen for this map (OR'd during scan).
        // Indexed by LayerType enum: bit 0 = minimap, bit 1 = map_texture, etc.
        // Avoids expensive joins to build_map_layers for the maps list endpoint.
        Execute.Sql("ALTER TABLE maps ADD COLUMN layer_mask SMALLINT;");
        Execute.Sql(@"
            UPDATE maps SET layer_mask = COALESCE((
                SELECT bit_or(1 << CASE bml.layer_type
                    WHEN 'minimap' THEN 0 WHEN 'map_texture' THEN 1
                    WHEN 'no_liquid' THEN 2 WHEN 'impass' THEN 3 WHEN 'area_id' THEN 4
                END)
                FROM build_map_layers bml WHERE bml.map_id = maps.id
            ), 0);
        ");
        Execute.Sql("ALTER TABLE maps ALTER COLUMN layer_mask SET NOT NULL;");
    }

    public override void Down() => throw new NotSupportedException();
}
