using FluentMigrator;

namespace Minimaps.Database.Migrations;

[Migration(2)]
public class SeparateProductCdns : Migration
{
    public override void Up()
    {
        // strip out any previous products that had scans in the exception state, in the initial collected dataset this is exclusively old 9.x builds
        // that have casc parsing errors. don't care about losing them.
        Execute.Sql("DELETE FROM product_compositions WHERE product_id IN (SELECT ps.product_id FROM product_scans ps WHERE ps.state = 'exception');");
        Execute.Sql("DELETE FROM product_scans WHERE state = 'exception';");
        Execute.Sql("DELETE FROM products WHERE id IN (SELECT p.id FROM products p LEFT JOIN product_scans ps ON ps.product_id = p.id WHERE ps.product_id IS NULL);");

        // new product source table
        Create.Table("product_sources")
            .WithColumn("id").AsInt64().Identity().PrimaryKey()
            .WithColumn("product_id").AsInt64().NotNullable()
            .WithColumn("config_build").AsString().NotNullable()
            .WithColumn("config_cdn").AsString().NotNullable()
            .WithColumn("config_product").AsString().NotNullable()
            .WithColumn("config_regions").AsCustom("TEXT[]").NotNullable()
            .WithColumn("first_seen").AsCustom("TIMESTAMPTZ").NotNullable();

        // one specific config combination per product
        Create.UniqueConstraint("UQ_product_sources_product_configs")
            .OnTable("product_sources")
            .Columns("product_id", "config_build", "config_cdn", "config_product");

        // temp table that we populate with the most recent product, we clean up others (but not after migrating data to product_sources)
        Execute.Sql(@"
            CREATE TEMP TABLE products_to_delete AS
            SELECT p.id FROM products p WHERE p.id NOT IN (
                SELECT MAX(id) FROM products GROUP BY build_id, product, config_build, config_product
            );");

        // Delete associated product_scans
        Execute.Sql(@"
            DELETE FROM product_scans ps_del
            WHERE ps_del.product_id IN (SELECT id FROM products_to_delete)
            AND EXISTS (
                -- added this check to ensure that we only delete scans if there's a kept product with the same state
                -- this way the migration will fail if my assumptions are incorrect and we won't lose any data...
                SELECT 1 FROM products p_keep
                INNER JOIN product_scans ps_keep ON ps_keep.product_id = p_keep.id
                INNER JOIN products p_del ON p_del.id = ps_del.product_id
                WHERE p_keep.build_id = p_del.build_id
                AND p_keep.product = p_del.product
                AND p_keep.config_build = p_del.config_build
                AND p_keep.config_product = p_del.config_product
                AND p_keep.id NOT IN (SELECT id FROM products_to_delete)
                AND ps_keep.state = ps_del.state
            );");

        // Migrate product sources keeping the most recent (max id)
        Execute.Sql(@"
            INSERT INTO product_sources (product_id, config_build, config_cdn, config_product, config_regions, first_seen)
            SELECT p_keep.id AS product_id, p_all.config_build, p_all.config_cdn, p_all.config_product, p_keep.config_regions, MIN(p_all.first_seen) AS first_seen
            FROM products p_all
            INNER JOIN (
                SELECT MAX(id) AS id, build_id, product, config_build, config_product, config_regions 
                FROM products
                GROUP BY build_id, product, config_build, config_product, config_regions
            ) p_keep 
            ON p_all.build_id = p_keep.build_id AND p_all.product = p_keep.product AND p_all.config_build = p_keep.config_build AND p_all.config_product = p_keep.config_product
            GROUP BY p_keep.id, p_all.config_build, p_all.config_cdn, p_all.config_product, p_keep.config_regions;");

        // FK product_sources to products
        Create.ForeignKey("FK_product_sources_product")
            .FromTable("product_sources").ForeignColumn("product_id")
            .ToTable("products").PrimaryColumn("id");

        // Consolidate regions
        Execute.Sql(@"
            UPDATE products p_keep SET config_regions = (
                SELECT ARRAY(
                    SELECT DISTINCT unnest(config_regions)
                    FROM products p_all
                    WHERE p_all.build_id = p_keep.build_id
                    AND p_all.product = p_keep.product
                    AND p_all.config_build = p_keep.config_build
                    AND p_all.config_product = p_keep.config_product
                    ORDER BY 1
                )
            );");

        // Delete product_sources for products we're about to delete
        Execute.Sql("DELETE FROM product_compositions WHERE product_id IN (SELECT id FROM products_to_delete);");
        Execute.Sql("DELETE FROM product_sources WHERE product_id IN (SELECT id FROM products_to_delete);");
        Execute.Sql("DELETE FROM products WHERE id IN (SELECT id FROM products_to_delete);");

        // Drop the old unique constraint, columns and constaint on build/product
        Delete.UniqueConstraint("UC_products_build_id_product_config_build_config_cdn_config_pro")
            .FromTable("products");

        Delete.Column("config_cdn").FromTable("products");
        Delete.Column("config_build").FromTable("products");
        Delete.Column("config_product").FromTable("products");

        Create.UniqueConstraint("UQ_products_build_id_product")
            .OnTable("products")
            .Columns("build_id", "product");
    }

    public override void Down()
    {
        throw new Exception("not supported");
    }
}
