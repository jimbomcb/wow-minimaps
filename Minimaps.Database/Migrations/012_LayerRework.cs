using FluentMigrator;

namespace Minimaps.Database.Migrations;

/// <summary>
/// Here we go...
/// Big cleanup, removing the concept of minimap-textures-first and instead working from a 
/// set of selectable base tile layers.
/// All build_maps now become a minimap layer.
/// layer_type Postgres ENUM, add cdn_missing, drop partial.
/// build_maps becomes a lightweight WDT existence table.
/// maps.first_minimap/last_minimap dropped (just build at runtime for now).
/// </summary>
[Migration(12)]
public class LayerRework : Migration
{
    public override void Up()
    {
        // layer_type enum: Npgsql translates DB snake_case to C# LayerType
        Execute.Sql("CREATE TYPE layer_type_enum AS ENUM ('minimap', 'map_texture', 'no_liquid', 'impass', 'area_id');");
        Execute.Sql("UPDATE build_map_layers SET layer_type = 'map_texture' WHERE layer_type = 'maptexture';");
        Execute.Sql("UPDATE build_map_layers SET layer_type = 'no_liquid' WHERE layer_type = 'noliquid';");
        Execute.Sql("UPDATE build_map_layers SET layer_type = 'area_id' WHERE layer_type = 'areaid';");

        // Drop the PK (uses old varchar layer_type), convert column, recreate
        Execute.Sql("ALTER TABLE build_map_layers DROP CONSTRAINT \"PK_build_map_layers\";");
        Execute.Sql("ALTER TABLE build_map_layers ALTER COLUMN layer_type TYPE layer_type_enum USING layer_type::layer_type_enum;");
        Execute.Sql("ALTER TABLE build_map_layers ADD CONSTRAINT \"PK_build_map_layers\" PRIMARY KEY (build_id, map_id, layer_type);");

        // cdn_missing hash array replaces partial
        Execute.Sql("ALTER TABLE build_map_layers ADD COLUMN cdn_missing BYTEA[];");
        Execute.Sql("ALTER TABLE build_map_layers DROP COLUMN partial;");

        // build_maps to build_map_layers
        Execute.Sql(@"
            INSERT INTO build_map_layers (build_id, map_id, layer_type, composition_hash)
            SELECT build_id, map_id, 'minimap'::layer_type_enum, composition_hash FROM build_maps
            WHERE composition_hash IS NOT NULL ON CONFLICT (build_id, map_id, layer_type) DO NOTHING;
        ");

        Execute.Sql("ALTER TABLE build_maps DROP CONSTRAINT \"FK_build_maps_composition\";");
        Execute.Sql("ALTER TABLE build_maps DROP COLUMN composition_hash;");
        Execute.Sql("ALTER TABLE build_maps RENAME COLUMN tiles TO wdt_tile_count;");
        Execute.Sql("ALTER TABLE maps DROP CONSTRAINT \"FK_maps_first_minimap\";");
        Execute.Sql("ALTER TABLE maps DROP CONSTRAINT \"FK_maps_last_minimap\";");
        Execute.Sql("ALTER TABLE maps DROP COLUMN first_minimap;");
        Execute.Sql("ALTER TABLE maps DROP COLUMN last_minimap;");

        // bye bye existing layers
        Execute.Sql("TRUNCATE build_map_layers;");
        Execute.Sql("TRUNCATE compositions CASCADE;");
        Execute.Sql("TRUNCATE data_blobs CASCADE;");

        // rescan
        Execute.Sql("UPDATE product_scans SET state = 'pending', exception = NULL WHERE state NOT IN ('encrypted_build', 'encrypted_map_database');");
    }

    public override void Down()
    {
        throw new NotSupportedException("good one.");
    }
}
