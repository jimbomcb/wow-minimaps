using FluentMigrator;

namespace Minimaps.Database.Migrations;

[Migration(11)]
public class BuildMapLayersDataHash : Migration
{
    public override void Up()
    {
        // data_hash column for data layers (impass, areaid) that reference data_blobs instead of compositions
        Alter.Table("build_map_layers")
            .AddColumn("data_hash").AsCustom("BYTEA").Nullable();

        // Make composition_hash nullable (data layers don't have compositions)
        Alter.Column("composition_hash").OnTable("build_map_layers").AsCustom("BYTEA").Nullable();

        Create.ForeignKey("FK_build_map_layers_data")
            .FromTable("build_map_layers").ForeignColumn("data_hash")
            .ToTable("data_blobs").PrimaryColumn("hash");

        // composition_hash or data_hash must be present
        Execute.Sql("ALTER TABLE build_map_layers ADD CONSTRAINT CK_build_map_layers_hash " +
            "CHECK ((composition_hash IS NOT NULL) != (data_hash IS NOT NULL))");
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE build_map_layers DROP CONSTRAINT CK_build_map_layers_hash");
        Delete.ForeignKey("FK_build_map_layers_data").OnTable("build_map_layers");
        Delete.Column("data_hash").FromTable("build_map_layers");
        Alter.Column("composition_hash").OnTable("build_map_layers").AsCustom("BYTEA").NotNullable();
    }
}
