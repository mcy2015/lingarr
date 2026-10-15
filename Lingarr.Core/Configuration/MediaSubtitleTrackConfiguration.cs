using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Lingarr.Core.Entities;

namespace Lingarr.Core.Configuration;

/// <summary>
/// Embedded subtitle tracks are looked up per owning media item (detail page
/// embedding section) and bulk-read during scans, so the composite
/// (media_type, media_id) index backs both paths.
/// </summary>
public class MediaSubtitleTrackConfiguration : IEntityTypeConfiguration<MediaSubtitleTrack>
{
    public void Configure(EntityTypeBuilder<MediaSubtitleTrack> builder)
    {
        builder.HasIndex(track => new { track.MediaType, track.MediaId })
            .HasDatabaseName("ix_media_subtitle_tracks_media_type_media_id");
    }
}
