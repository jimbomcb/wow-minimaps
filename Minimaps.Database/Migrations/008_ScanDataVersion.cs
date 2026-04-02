using FluentMigrator;

namespace Minimaps.Database.Migrations;

[Migration(8)]
public class ScanDataVersion : Migration
{
    public override void Up()
    {
        Alter.Table("product_scans")
            .AddColumn("data_version").AsInt32().WithDefaultValue(1)
            .AddColumn("data_version_attempt").AsInt32().WithDefaultValue(1)
            .AddColumn("data_version_error").AsString().Nullable();
    }

    public override void Down()
    {
        Delete.Column("data_version").FromTable("product_scans");
        Delete.Column("data_version_attempt").FromTable("product_scans");
        Delete.Column("data_version_error").FromTable("product_scans");
    }
}