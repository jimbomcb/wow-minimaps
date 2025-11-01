using FluentMigrator;

namespace Minimaps.Database.Migrations;

[Migration(4)]
public class MapHasTiles : Migration
{
    public override void Up()
    {
        Alter.Table("maps")
            .AddColumn("first_build_id").AsInt64().Nullable()
            .AddColumn("last_build_id").AsInt64().Nullable();

        Execute.Sql(@"
            WITH first_build AS (
                SELECT DISTINCT ON (map_id)
                    map_id,
                    build_id
                FROM build_maps
                WHERE composition_hash IS NOT NULL
                ORDER BY map_id, build_id ASC
            )
            UPDATE maps m
            SET first_build_id = fb.build_id
            FROM first_build fb
            WHERE m.id = fb.map_id;

            WITH last_build AS (
                SELECT DISTINCT ON (map_id)
                    map_id,
                    build_id
                FROM build_maps
                WHERE composition_hash IS NOT NULL
                ORDER BY map_id, build_id DESC
            )
            UPDATE maps m
            SET last_build_id = lb.build_id
            FROM last_build lb
            WHERE m.id = lb.map_id;
        ");

        Create.ForeignKey("FK_maps_first_build_map")
            .FromTable("maps").ForeignColumns("id", "first_build_id")
            .ToTable("build_maps").PrimaryColumns("map_id", "build_id");

        Create.ForeignKey("FK_maps_last_build_map")
            .FromTable("maps").ForeignColumns("id", "last_build_id")
            .ToTable("build_maps").PrimaryColumns("map_id", "build_id");

        Delete.Column("first_composition").FromTable("maps");
        Delete.Column("last_composition").FromTable("maps");
    }

    public override void Down()
    {
        Delete.ForeignKey("FK_maps_first_build_map").OnTable("maps");
        Delete.ForeignKey("FK_maps_last_build_map").OnTable("maps");

        Delete.Column("first_build_id").FromTable("maps");
        Delete.Column("last_build_id").FromTable("maps");

        Alter.Table("maps")
            .AddColumn("first_composition").AsCustom("BYTEA").Nullable()
            .AddColumn("last_composition").AsCustom("BYTEA").Nullable();
    }
}
