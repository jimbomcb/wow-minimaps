using FluentMigrator;

namespace Minimaps.Database.Migrations;

[Migration(14)]
public class AdtExtractionsLayerTypeEnum : Migration
{
    public override void Up()
    {
        // Migrate adt_data_extractions too like the layers
        Execute.Sql("UPDATE adt_data_extractions SET layer_type = 'area_id' WHERE layer_type = 'areaid';");
        Execute.Sql("ALTER TABLE adt_data_extractions DROP CONSTRAINT \"PK_adt_data_extractions\";");
        Execute.Sql("ALTER TABLE adt_data_extractions ALTER COLUMN layer_type TYPE layer_type_enum USING layer_type::layer_type_enum;");
        Execute.Sql("ALTER TABLE adt_data_extractions ADD CONSTRAINT \"PK_adt_data_extractions\" PRIMARY KEY (adt_hash, layer_type);");
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE adt_data_extractions DROP CONSTRAINT \"PK_adt_data_extractions\";");
        Execute.Sql("ALTER TABLE adt_data_extractions ALTER COLUMN layer_type TYPE VARCHAR USING layer_type::TEXT;");
        Execute.Sql("ALTER TABLE adt_data_extractions ADD CONSTRAINT \"PK_adt_data_extractions\" PRIMARY KEY (adt_hash, layer_type);");
        Execute.Sql("UPDATE adt_data_extractions SET layer_type = 'areaid' WHERE layer_type = 'area_id';");
    }
}
