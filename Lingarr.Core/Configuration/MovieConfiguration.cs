using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Lingarr.Core.Entities;

namespace Lingarr.Core.Configuration;

public class MovieConfiguration : IEntityTypeConfiguration<Movie>
{
    public void Configure(EntityTypeBuilder<Movie> builder)
    {
        builder
            .HasMany(m => m.Images)
            .WithOne(i => i.Movie)
            .HasForeignKey(i => i.MovieId)
            .OnDelete(DeleteBehavior.Cascade);
        
        builder.Navigation(m => m.Images)
            .AutoInclude();
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