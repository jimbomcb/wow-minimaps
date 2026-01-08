using FluentMigrator;

namespace Minimaps.Database.Migrations;

[Migration(6)]
public class DropProductCompositions : Migration
{
    public override void Up()
    {
        // Dropping in favour of just deriving the compositions of a product via build_maps, which houses the per-map compositions per build directly.
        // It's true that we lose the ability to tell which product contributed which composition directly, but can just join.

        Delete.ForeignKey("FK_product_compositions_product").OnTable("product_compositions");
        Delete.ForeignKey("FK_product_compositions_composition").OnTable("product_compositions");
        Delete.Table("product_compositions");
    }

    public override void Down()
    {
        Create.Table("product_compositions")
            .WithColumn("composition_hash").AsCustom("BYTEA")
            .WithColumn("product_id").AsInt64();

        Create.PrimaryKey("PK_product_compositions")
            .OnTable("product_compositions")
            .Columns("composition_hash", "product_id");

        Create.ForeignKey("FK_product_compositions_product")
            .FromTable("product_compositions").ForeignColumn("product_id")
            .ToTable("products").PrimaryColumn("id");

        Create.ForeignKey("FK_product_compositions_composition")
            .FromTable("product_compositions").ForeignColumn("composition_hash")
            .ToTable("compositions").PrimaryColumn("hash");
    }
}
