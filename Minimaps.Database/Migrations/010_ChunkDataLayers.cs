using FluentMigrator;

namespace Minimaps.Database.Migrations;

[Migration(10)]
public class ChunkDataLayers : Migration
{
    public override void Up()
    {
        // Content-addressed blob store for extracted ADT chunk data (impass flags, area IDs, etc)
        // brotli compressed at rest.
        // Anything large enough goes out to the separate content store, anything small is just stored here in the DB
        Create.Table("data_blobs")
            .WithColumn("hash").AsCustom("BYTEA").PrimaryKey()
            .WithColumn("data").AsCustom("BYTEA").NotNullable();

        // Maps ADT content hashes to their extracted data blob hashes, per layer type.
        // Two-hash dedup: ADT file changes -> re-extract -> if data unchanged, points to existing blob.
        Create.Table("adt_data_extractions")
            .WithColumn("adt_hash").AsCustom("BYTEA")
            .WithColumn("layer_type").AsString(50)
            .WithColumn("data_hash").AsCustom("BYTEA").NotNullable();

        Create.PrimaryKey("PK_adt_data_extractions")
            .OnTable("adt_data_extractions")
            .Columns("adt_hash", "layer_type");

        Create.ForeignKey("FK_adt_data_extractions_data")
            .FromTable("adt_data_extractions").ForeignColumn("data_hash")
            .ToTable("data_blobs").PrimaryColumn("hash");
    }

    public override void Down()
    {
        Delete.Table("adt_data_extractions");
        Delete.Table("data_blobs");
    }
}
