using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Lingarr.Core.Entities;

namespace Lingarr.Core.Configuration;

public class EpisodeConfiguration : IEntityTypeConfiguration<Episode>
{
    public void Configure(EntityTypeBuilder<Episode> builder)
    {
        builder
            .HasOne(e => e.Season)
            .WithMany(s => s.Episodes)
            .HasForeignKey(e => e.SeasonId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(e => e.Season).AutoInclude();
        // AudioLanguages persists as a JSON TEXT column (M0007); null means
        // "unknown", so nulls pass through untouched and the converter only
        // round-trips actual arrays.
        builder.Property(e => e.AudioLanguages)
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v),
                s => string.IsNullOrEmpty(s)
                    ? Array.Empty<string>()
                    : System.Text.Json.JsonSerializer.Deserialize<string[]>(s)!)
            .HasColumnType("TEXT");

    }
}