using FluentMigrator;

namespace Minimaps.Database.Migrations;

[Migration(5)]
public class RenameMapVersionColumns : Migration
{
    public override void Up()
    {
        Rename.Column("first_version").OnTable("maps").To("first_seen");
        Rename.Column("last_version").OnTable("maps").To("last_seen");

        Rename.Column("first_build_id").OnTable("maps").To("first_minimap");
        Rename.Column("last_build_id").OnTable("maps").To("last_minimap");

        Delete.ForeignKey("FK_maps_first_build_map").OnTable("maps");
        Delete.ForeignKey("FK_maps_last_build_map").OnTable("maps");

        Create.ForeignKey("FK_maps_first_minimap")
            .FromTable("maps").ForeignColumns("id", "first_minimap")
            .ToTable("build_maps").PrimaryColumns("map_id", "build_id");

        Create.ForeignKey("FK_maps_last_minimap")
            .FromTable("maps").ForeignColumns("id", "last_minimap")
            .ToTable("build_maps").PrimaryColumns("map_id", "build_id");
    }

    public override void Down()
    {
        Delete.ForeignKey("FK_maps_first_minimap").OnTable("maps");
        Delete.ForeignKey("FK_maps_last_minimap").OnTable("maps");

        Rename.Column("first_seen").OnTable("maps").To("first_version");
        Rename.Column("last_seen").OnTable("maps").To("last_version");

        Rename.Column("first_minimap").OnTable("maps").To("first_build_id");
        Rename.Column("last_minimap").OnTable("maps").To("last_build_id");

        Create.ForeignKey("FK_maps_first_build_map")
            .FromTable("maps").ForeignColumns("id", "first_build_id")
            .ToTable("build_maps").PrimaryColumns("map_id", "build_id");

        Create.ForeignKey("FK_maps_last_build_map")
            .FromTable("maps").ForeignColumns("id", "last_build_id")
            .ToTable("build_maps").PrimaryColumns("map_id", "build_id");
    }
}
