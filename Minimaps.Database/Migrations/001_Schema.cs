using FluentMigrator;

namespace Minimaps.Database.Migrations;

[Migration(1)]
public class InitialSchema : Migration
{
    public override void Up()
    {
        // PL/pgSQL versions of the BuildVersion encoding, mainly just so I can add db level validation that the ID & version string always match
        Execute.Sql(@"
            CREATE OR REPLACE FUNCTION encode_build_version(version_string TEXT) RETURNS BIGINT LANGUAGE plpgsql IMMUTABLE 
            AS $$
            DECLARE
                parts TEXT[] := string_to_array(version_string, '.');
                expansion INT := parts[1]::INT;
                major INT := parts[2]::INT;
                minor INT := parts[3]::INT;
                build BIGINT := parts[4]::BIGINT;
            BEGIN
                IF array_length(parts, 1) != 4 THEN RAISE EXCEPTION 'Version must be expansion.major.minor.build'; END IF;
                IF expansion NOT BETWEEN 0 AND 4095 THEN RAISE EXCEPTION 'Expansion must be 0-4095'; END IF;
                IF major NOT BETWEEN 0 AND 1023 THEN RAISE EXCEPTION 'Major must be 0-1023'; END IF;
                IF minor NOT BETWEEN 0 AND 1023 THEN RAISE EXCEPTION 'Minor must be 0-1023'; END IF;
                IF build NOT BETWEEN 0 AND 4294967295 THEN RAISE EXCEPTION 'Build must be 0-4294967295'; END IF;
                RETURN (expansion::BIGINT << 52) | (major::BIGINT << 42) | (minor::BIGINT << 32) | build;
            END;
            $$;
            CREATE OR REPLACE FUNCTION decode_build_version(encoded_value BIGINT) RETURNS TEXT LANGUAGE plpgsql IMMUTABLE
            AS $$
            BEGIN
                RETURN ((encoded_value >> 52) & 4095) || '.' || 
                       ((encoded_value >> 42) & 1023) || '.' || 
                       ((encoded_value >> 32) & 1023) || '.' || 
                       (encoded_value & 4294967295);
            END;
            $$;");

        Create.Table("builds")
            .WithColumn("id").AsInt64().PrimaryKey()
            .WithColumn("version").AsString().NotNullable()
            .WithColumn("locked").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("encrypted_maps").AsCustom("jsonb").Nullable();

        // Add check constraint to ensure version string matches encoded ID
        Execute.Sql(@"
            ALTER TABLE builds ADD CONSTRAINT builds_version_matches_id CHECK (encode_build_version(version) = id);
        ");

        Create.Index("IX_builds_version")
            .OnTable("builds")
            .OnColumn("version");

        Create.Table("build_products")
            .WithColumn("build_id").AsInt64().NotNullable()
            .WithColumn("product").AsString(50).NotNullable()
            .WithColumn("released").AsDateTime().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("config_build").AsString().NotNullable()
            .WithColumn("config_cdn").AsString().NotNullable()
            .WithColumn("config_product").AsString().NotNullable()
            .WithColumn("config_regions").AsCustom("TEXT[]").NotNullable();

        Create.PrimaryKey("PK_build_products")
            .OnTable("build_products")
            .Columns("build_id", "product");

        Create.ForeignKey("FK_build_products_build")
            .FromTable("build_products").ForeignColumn("build_id")
            .ToTable("builds").PrimaryColumn("id");

        Create.Index("IX_build_products_product")
            .OnTable("build_products")
            .OnColumn("product");

        Create.Table("maps")
            .WithColumn("id").AsInt32().PrimaryKey()
            .WithColumn("json").AsCustom("jsonb").Nullable() // Latest version raw database row
            .WithColumn("directory").AsString().Nullable() // Most recent directory
            .WithColumn("name").AsString().Nullable() // Most recent name
            .WithColumn("name_history").AsCustom("jsonb").Nullable();

        Execute.Sql(@"ALTER TABLE maps ADD COLUMN parent INT4 
            GENERATED ALWAYS AS (COALESCE((json->>'CosmeticParentMapID')::INT4, (json->>'ParentMapID')::INT4)) VIRTUAL");

        //Create.Index("IX_maps_parent")
        //    .OnTable("maps")
        //    .OnColumn("parent");
        //
        //Create.Index("IX_maps_directory")
        //    .OnTable("maps")
        //    .OnColumn("directory");
        //
        //Create.Index("IX_maps_name")
        //    .OnTable("maps")
        //    .OnColumn("name");

        // Build-Map relationship table
        Create.Table("build_maps")
            .WithColumn("build_id").AsInt64().NotNullable()
            .WithColumn("map_id").AsInt32().NotNullable();

        Create.PrimaryKey("PK_build_maps")
            .OnTable("build_maps")
            .Columns("build_id", "map_id");

        Create.ForeignKey("FK_build_maps_build")
            .FromTable("build_maps").ForeignColumn("build_id")
            .ToTable("builds").PrimaryColumn("id");

        Create.ForeignKey("FK_build_maps_map")
            .FromTable("build_maps").ForeignColumn("map_id")
            .ToTable("maps").PrimaryColumn("id");

        Create.Index("IX_build_maps_build_id")
            .OnTable("build_maps")
            .OnColumn("build_id");

        Create.Index("IX_build_maps_map_id")
            .OnTable("build_maps")
            .OnColumn("map_id");

        // Minimap tiles
        Create.Table("minimap_tiles")
            .WithColumn("hash").AsString(32).PrimaryKey()
            .WithColumn("first_seen").AsDateTime().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);
        Execute.Sql("ALTER TABLE minimap_tiles ADD CONSTRAINT minimap_tiles_hash_upperhex_only CHECK (hash ~ '^[A-F0-9]+$');");
    }

    public override void Down()
    {
        Delete.Table("build_maps");
        Delete.Table("build_products");
        Delete.Table("maps");
        Delete.Table("minimap_tiles");
        Delete.Table("builds");

        Execute.Sql("DROP FUNCTION IF EXISTS encode_build_version(TEXT);");
        Execute.Sql("DROP FUNCTION IF EXISTS decode_build_version(BIGINT);");
    }
}