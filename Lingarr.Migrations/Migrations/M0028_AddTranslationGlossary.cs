using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

/// <summary>
/// Terminology table for translation and proofreading (A8): locked entries
/// are deterministically enforced after machine translation, advisory ones
/// hint in the proofreading workbench.
/// </summary>
[Migration(28)]
public class M0028_AddTranslationGlossary : Migration
{
    public override void Up()
    {
        if (!Schema.Table("translation_glossaries").Exists())
        {
            Create.Table("translation_glossaries")
                .WithColumn("id").AsInt32().PrimaryKey().Identity()
                .WithColumn("source_term").AsString(500).NotNullable()
                .WithColumn("target_term").AsString(500).NotNullable()
                .WithColumn("note").AsString(1000).Nullable()
                .WithColumn("is_locked").AsBoolean().NotNullable().WithDefaultValue(false)
                .WithColumn("created_at").AsDateTime().NotNullable()
                .WithColumn("updated_at").AsDateTime().NotNullable();

            Create.Index("ix_glossary_source_term").OnTable("translation_glossaries")
                .OnColumn("source_term");
        }
    }

    public override void Down()
    {
        if (Schema.Table("translation_glossaries").Exists())
        {
            Delete.Table("translation_glossaries");
        }
    }
}
