using FluentMigrator;

namespace Minimaps.Database.Migrations;

[Migration(1)]
public class InitialSchema : Migration
{
    public override void Up()
    {
        // PL/pgSQL versions of the BuildVersion encoding/decoding, mainly just so I can add db level validation that the ID & version string always match
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
            .WithColumn("version").AsString()
            .WithColumn("discovered").AsCustom("TIMESTAMPTZ").WithDefault(SystemMethods.CurrentUTCDateTime);
        // Deciding if it's worth keeping the version string, for now ensure that if it does exist that it's in sync with the ID
        Execute.Sql(@"ALTER TABLE builds ADD CONSTRAINT builds_version_matches_id CHECK (encode_build_version(version) = id);");

        Create.Table("products")
            .WithColumn("id").AsInt64().Identity().PrimaryKey()
            .WithColumn("build_id").AsInt64()
            .WithColumn("product").AsString(50)
            .WithColumn("config_build").AsString()
            .WithColumn("config_cdn").AsString()
            .WithColumn("config_product").AsString()
            .WithColumn("config_regions").AsCustom("TEXT[]")
            .WithColumn("first_seen").AsCustom("TIMESTAMPTZ").WithDefault(SystemMethods.CurrentUTCDateTime);
        // Ensure we don't end up with the same region multiple times (done via trigger as check constraint can't do subquery)
        Execute.Sql(@"CREATE OR REPLACE FUNCTION ensure_unique_regions() RETURNS TRIGGER LANGUAGE plpgsql AS $$ BEGIN " +
                "IF array_length(NEW.config_regions, 1) != (SELECT count(DISTINCT elem) FROM unnest(NEW.config_regions) AS elem) THEN " +
                    "RAISE EXCEPTION 'products contains duplicate region, must be unique'; " +
                "END IF; " +
                "RETURN NEW; " +
            "END; $$;" +
            "CREATE TRIGGER trigger_unique_regions BEFORE INSERT OR UPDATE ON products FOR EACH ROW EXECUTE FUNCTION ensure_unique_regions();");

        Create.UniqueConstraint().OnTable("products")
            .Columns("build_id", "product", "config_build", "config_cdn", "config_product");

        Create.ForeignKey("FK_products_build")
            .FromTable("products").ForeignColumn("build_id")
            .ToTable("builds").PrimaryColumn("id");

        Execute.Sql(@"
            CREATE TYPE scan_state AS ENUM (
                'pending',
                'exception', 
                'encrypted_build',
                'encrypted_map_database',
                'partial_decrypt',
                'full_decrypt'
            );
        ");

        Create.Table("product_scans")
            .WithColumn("product_id").AsInt64().PrimaryKey()
            .WithColumn("state").AsCustom("scan_state").WithDefaultValue("pending")
            .WithColumn("last_scanned").AsCustom("TIMESTAMPTZ").WithDefault(SystemMethods.CurrentUTCDateTime)
            .WithColumn("scan_time").AsCustom("INTERVAL").Nullable()
            .WithColumn("exception").AsString().Nullable()
            .WithColumn("encrypted_key").AsString().Nullable()
            .WithColumn("encrypted_maps").AsCustom("jsonb").Nullable();

        Create.ForeignKey("FK_product_scans_build")
                .FromTable("product_scans").ForeignColumn("product_id")
                .ToTable("products").PrimaryColumn("id");

        Create.Table("maps")
            .WithColumn("id").AsInt32().PrimaryKey()
            .WithColumn("json").AsCustom("jsonb")
            .WithColumn("directory").AsString()
            .WithColumn("name").AsString()
            .WithColumn("first_version").AsInt64()
            .WithColumn("last_version").AsInt64()
            .WithColumn("name_history").AsCustom("jsonb");

        Execute.Sql(@"ALTER TABLE maps ADD COLUMN parent INT4 GENERATED ALWAYS AS (COALESCE((json->>'CosmeticParentMapID')::INT4, (json->>'ParentMapID')::INT4)) VIRTUAL");
        Create.ForeignKey("FK_maps_first_version").FromTable("maps").ForeignColumn("first_version").ToTable("builds").PrimaryColumn("id");
        Create.ForeignKey("FK_maps_last_version").FromTable("maps").ForeignColumn("last_version").ToTable("builds").PrimaryColumn("id");

        Execute.Sql(@"CREATE OR REPLACE FUNCTION dedupe_maps_name_history() RETURNS TRIGGER LANGUAGE plpgsql AS $$ 
BEGIN
    NEW.name_history := (
        WITH ordered_history AS (
            SELECT key::BIGINT as build_id, value as name
            FROM jsonb_each_text(NEW.name_history)
            WHERE value IS NOT NULL  -- Filter out null names
            ORDER BY key::BIGINT
        ),
        changes_only AS (
            SELECT build_id, name
            FROM (
                SELECT build_id, name,
                       LAG(name) OVER (ORDER BY build_id) as prev_name,
                       ROW_NUMBER() OVER (ORDER BY build_id) as rn
                FROM ordered_history
            ) t
            WHERE rn = 1 OR name != prev_name  -- Keep first entry or when name changes
        )
        SELECT jsonb_object_agg(build_id::TEXT, name ORDER BY build_id)
        FROM changes_only
    );
    
    RETURN NEW;
END;$$;
CREATE TRIGGER trigger_maps_name_history_dedupe BEFORE INSERT OR UPDATE ON maps FOR EACH ROW EXECUTE FUNCTION dedupe_maps_name_history();");

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
            .WithColumn("build_id").AsInt64()
            .WithColumn("map_id").AsInt32();

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
            .WithColumn("hash").AsCustom("BYTEA").PrimaryKey()
            .WithColumn("first_seen").AsCustom("TIMESTAMPTZ").NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.Table("tact_keys")
            .WithColumn("key_name").AsString(16).PrimaryKey()
            .WithColumn("key").AsCustom("BYTEA").NotNullable()
            .WithColumn("discovered").AsCustom("TIMESTAMPTZ").WithDefault(SystemMethods.CurrentUTCDateTime);

        // K/V store
        Create.Table("settings")
            .WithColumn("key").AsString().PrimaryKey()
            .WithColumn("value").AsString().Nullable();

        Create.Table("compositions")
            .WithColumn("hash").AsCustom("BYTEA").PrimaryKey()
            .WithColumn("composition").AsCustom("JSONB")
            .WithColumn("tiles").AsInt32()
            .WithColumn("extents").AsCustom("JSONB").Nullable();

        Create.Table("build_minimaps")
            .WithColumn("build_id").AsInt64()
            .WithColumn("map_id").AsInt32()
            .WithColumn("composition_hash").AsCustom("BYTEA");

        Create.PrimaryKey("PK_build_minimap")
            .OnTable("build_minimaps")
            .Columns("build_id", "map_id");

        Create.ForeignKey("FK_build_minimap_composition")
            .FromTable("build_minimaps").ForeignColumn("composition_hash")
            .ToTable("compositions").PrimaryColumn("hash");

        Create.ForeignKey("FK_build_minimap_map")
            .FromTable("build_minimaps").ForeignColumn("map_id")
            .ToTable("maps").PrimaryColumn("id");

        Create.ForeignKey("FK_build_minimap_build")
            .FromTable("build_minimaps").ForeignColumn("build_id")
            .ToTable("builds").PrimaryColumn("id");
    }

    public override void Down()
    {
        throw new Exception("not supported");
    }
}