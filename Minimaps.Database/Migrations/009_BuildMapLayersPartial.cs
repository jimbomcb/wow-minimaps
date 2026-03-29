using FluentMigrator;

namespace Minimaps.Database.Migrations;

[Migration(9)]
public class BuildMapLayersPartial : Migration
{
    public override void Up()
    {
        Alter.Table("build_map_layers")
            .AddColumn("partial").AsBoolean().WithDefaultValue(false);
    }

    public override void Down()
    {
        Delete.Column("partial").FromTable("build_map_layers");
    }
}
