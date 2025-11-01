using FluentMigrator;

namespace Minimaps.Database.Migrations;

[Migration(3)]
public class MapCompositions : Migration
{
    public override void Up()
    {
        Alter.Table("maps")
            .AddColumn("first_composition").AsCustom("BYTEA").Nullable()
            .AddColumn("last_composition").AsCustom("BYTEA").Nullable();

        Execute.Sql(@"
            UPDATE maps m SET first_composition = (
                SELECT bm.composition_hash FROM build_maps bm
                WHERE bm.map_id = m.id
                AND bm.composition_hash IS NOT NULL
                ORDER BY bm.build_id ASC
                LIMIT 1
            );");

        Execute.Sql(@"
            UPDATE maps m SET last_composition = (
                SELECT bm.composition_hash FROM build_maps bm
                WHERE bm.map_id = m.id
                AND bm.composition_hash IS NOT NULL
                ORDER BY bm.build_id DESC
                LIMIT 1
            );");
    }

    public override void Down()
    {
        Delete.Column("first_composition").FromTable("maps");
        Delete.Column("last_composition").FromTable("maps");
    }
}
