using FluentMigrator;

namespace Minimaps.Database.Migrations;

[Migration(7)]
public class BuildMapLayers : Migration
{
    public override void Up()
    {
        // Compositions now get referenced by either the originl build_map OR a new build_map_layer entry.
        Create.Table("build_map_layers")
            .WithColumn("build_id").AsInt64()
            .WithColumn("map_id").AsInt32()
            .WithColumn("layer_type").AsString(50)
            .WithColumn("composition_hash").AsCustom("BYTEA");

        Create.PrimaryKey("PK_build_map_layers")
            .OnTable("build_map_layers")
            .Columns("build_id", "map_id", "layer_type");

        Create.ForeignKey("FK_build_map_layers_build")
            .FromTable("build_map_layers").ForeignColumn("build_id")
            .ToTable("builds").PrimaryColumn("id");

        Create.ForeignKey("FK_build_map_layers_map")
            .FromTable("build_map_layers").ForeignColumn("map_id")
            .ToTable("maps").PrimaryColumn("id");

        Create.ForeignKey("FK_build_map_layers_composition")
            .FromTable("build_map_layers").ForeignColumn("composition_hash")
            .ToTable("compositions").PrimaryColumn("hash");

        Create.Index("IX_build_map_layers_map_id")
            .OnTable("build_map_layers")
            .OnColumn("map_id");
    }

    public override void Down()
    {
        Delete.Table("build_map_layers");
    }
}
