using FluentMigrator;

namespace Minimaps.Database.Migrations;

[Migration(1)]
public class InitialSchema : Migration
{
    public override void Up()
    {
        Create.Table("builds")
            .WithColumn("product").AsString(50)
            .WithColumn("version").AsString(50)
            .WithColumn("ver_expansion").AsInt32().NotNullable()
            .WithColumn("ver_major").AsInt32().NotNullable()
            .WithColumn("ver_minor").AsInt32().NotNullable()
            .WithColumn("ver_build").AsInt32().NotNullable()
            .WithColumn("processed").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("published").AsDateTime().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

        Create.PrimaryKey("PK_builds")
            .OnTable("builds")
            .Columns("version", "product");

        Create.Table("minimap_tiles")
            .WithColumn("hash").AsString(32).PrimaryKey();

        Create.Table("map")
            .WithColumn("id").AsInt32().PrimaryKey()
            .WithColumn("db2").AsCustom("jsonb").Nullable()
            .WithColumn("directory").AsString(100).Nullable()
            .WithColumn("name").AsString(100).Nullable()
            .WithColumn("parent").AsInt32().Nullable();

#if false

        Create.Table("map")
            .WithColumn("id").AsInt32().PrimaryKey()
            .WithColumn("db2").AsCustom("jsonb").Nullable()
            .WithColumn("directory").AsString(100).Nullable()
            .WithColumn("name").AsString(100).Nullable()
            .WithColumn("parent").AsInt32().Nullable();

        //Create.ForeignKey("FK_map_parent") // probably nullable given we don't know for sure if the parent map exists/might be deleted?
        //    .FromTable("map").ForeignColumn("parent")
        //    .ToTable("map").PrimaryColumn("id");

        Create.Table("minimap_tile")
            .WithColumn("hash").AsString(32).PrimaryKey()
            .WithColumn("first_seen").AsDateTime().NotNullable().WithDefault(SystemMethods.CurrentDateTime);

        Create.Table("minimap")
            .WithColumn("hash").AsString(32).NotNullable()
            .WithColumn("map_id").AsInt32().NotNullable()
            .WithColumn("tiles_json").AsCustom("jsonb").NotNullable();

        Create.PrimaryKey("PK_minimap")
            .OnTable("minimap")
            .Columns("hash", "map_id");

        Create.ForeignKey("FK_minimap_map")
            .FromTable("minimap").ForeignColumn("map_id")
            .ToTable("map").PrimaryColumn("id");

        Create.Table("build_minimap")
            .WithColumn("build_version").AsString(50).NotNullable()
            .WithColumn("map_id").AsInt32().NotNullable()
            .WithColumn("minimap_hash").AsString(32).NotNullable();

        Create.PrimaryKey("PK_build_minimap")
            .OnTable("build_minimap")
            .Columns("build_version", "map_id");

        Create.ForeignKey("FK_build_minimap_build")
            .FromTable("build_minimap").ForeignColumn("build_version")
            .ToTable("build").PrimaryColumns("version", "product");

        Create.ForeignKey("FK_build_minimap_map")
            .FromTable("build_minimap").ForeignColumn("map_id")
            .ToTable("map").PrimaryColumn("id");

        // todo: think more about query patterns...
        //Create.Index("IX_build_product")
        //    .OnTable("build")
        //    .OnColumn("product");
        //
        //Create.Index("IX_map_directory")
        //    .OnTable("map")
        //    .OnColumn("directory");
        //
        //Create.Index("IX_build_minimap_minimap_hash")
        //    .OnTable("build_minimap")
        //    .OnColumn("minimap_hash");
#endif
    }

    public override void Down()
    {
        //Delete.Table("build_minimap");
        //Delete.Table("minimap");
        Delete.Table("minimap_tile");
        //Delete.Table("map");
        Delete.Table("build");
    }
}