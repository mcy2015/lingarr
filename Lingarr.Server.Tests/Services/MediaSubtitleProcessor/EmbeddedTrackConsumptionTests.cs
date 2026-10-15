using Lingarr.Contracts.Models;
using Lingarr.Core.Configuration;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Models.FileSystem;
using Microsoft.EntityFrameworkCore;
using System;
using System.Threading;
using Moq;
using Xunit;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Lingarr.Server.Tests.Services.MediaSubtitleProcessor;

/// <summary>
/// F2b automation behaviour of <see cref="Lingarr.Server.Modules.Media.MediaSubtitleProcessor"/>:
/// embedded text tracks join the source candidate pool only when
/// <c>consume_embedded_tracks</c> is on; extraction is deferred to the point
/// where an embedded entry is actually selected as the source; covered
/// targets stay excluded through the merged available-language set.
/// </summary>
public class EmbeddedTrackConsumptionTests : MediaSubtitleProcessorTestBase
{
    private async Task<Movie> CreateMovieWithEmbeddedTrack(
        string language = "en",
        bool isTextBased = true,
        string codec = "srt")
    {
        var movie = await CreateTestMovie();
        DbContext.MediaSubtitleTracks.Add(new MediaSubtitleTrack
        {
            MediaType = MediaType.Movie,
            MediaId = movie.Id,
            StreamIndex = 2,
            Language = language,
            RawLanguage = language,
            Title = null,
            Codec = codec,
            IsTextBased = isTextBased,
            FilePath = "/movies/test/test.movie.mkv"
        });
        // scan-marker row must never become a candidate
        DbContext.MediaSubtitleTracks.Add(new MediaSubtitleTrack
        {
            MediaType = MediaType.Movie,
            MediaId = movie.Id,
            StreamIndex = MediaSubtitleTrack.ScanMarkerStreamIndex,
            Language = "fr",
            RawLanguage = "fr",
            Title = null,
            Codec = "srt",
            IsTextBased = true,
            FilePath = "/movies/test/test.movie.mkv"
        });
        await DbContext.SaveChangesAsync();
        return movie;
    }

    private void ExternalSubtitles(params Subtitles[] subtitles) =>
        SubtitleServiceMock
            .Setup(service => service.GetSubtitles("/movies/test", "test.movie"))
            .ReturnsAsync(subtitles.ToList());

    private void EnableEmbeddedConsumption() =>
        SettingServiceMock
            .Setup(service => service.GetSetting(SettingKeys.Translation.ConsumeEmbeddedTracks))
            .ReturnsAsync("true");

    [Fact]
    public async Task ProcessMedia_DefaultOff_IgnoresEmbeddedTracksEntirely()
    {
        var movie = await CreateMovieWithEmbeddedTrack();
        SetupStandardSettings();
        ExternalSubtitles(); // no external files at all
        EmbeddedTrackServiceMock
            .Setup(service => service.PrepareTranslationSource(
                It.IsAny<MediaType>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/movies/test/test.movie.en.srt");

        var processed = await Processor.ProcessMedia(movie, MediaType.Movie);

        // feature flag off: an embedded-only library processes nothing
        Assert.False(processed);
        TranslationRequestServiceMock.Verify(
            service => service.CreateRequest(It.IsAny<TranslateAbleSubtitle>()), Times.Never);
        EmbeddedTrackServiceMock.Verify(
            service => service.PrepareTranslationSource(
                It.IsAny<MediaType>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_FlagOn_EmbeddedOnlyMedia_TranslatesFromExtractedTrack()
    {
        var movie = await CreateMovieWithEmbeddedTrack();
        SetupStandardSettings();
        ExternalSubtitles(); // external list empty — embedded is all there is
        EnableEmbeddedConsumption();
        EmbeddedTrackServiceMock
            .Setup(service => service.PrepareTranslationSource(MediaType.Movie, 1, 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync("/movies/test/test.movie.en.srt");

        var processed = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.True(processed);
        EmbeddedTrackServiceMock.Verify(
            service => service.PrepareTranslationSource(MediaType.Movie, 1, 2, It.IsAny<CancellationToken>()),
            Times.Once);
        TranslationRequestServiceMock.Verify(
            service => service.CreateRequest(It.Is<TranslateAbleSubtitle>(request =>
                request.SubtitlePath == "/movies/test/test.movie.en.srt"
                && request.SourceLanguage == "en"
                && request.TargetLanguage == "ro"
                && request.MediaType == MediaType.Movie)),
            Times.Once);
    }

    [Fact]
    public async Task ProcessMedia_FlagOn_TargetAlreadyEmbedded_IsNotTranslated()
    {
        // embedded EN source + embedded RO track: RO (the configured target)
        // is covered by the merged candidate list, so nothing may be queued
        var movie = await CreateMovieWithEmbeddedTrack();
        DbContext.MediaSubtitleTracks.Add(new MediaSubtitleTrack
        {
            MediaType = MediaType.Movie,
            MediaId = movie.Id,
            StreamIndex = 3,
            Language = "ro",
            RawLanguage = "ro",
            Title = null,
            Codec = "srt",
            IsTextBased = true,
            FilePath = "/movies/test/test.movie.mkv"
        });
        await DbContext.SaveChangesAsync();
        SetupStandardSettings(ignoreCaptions: "false");
        ExternalSubtitles();
        EnableEmbeddedConsumption();

        var processed = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.False(processed);
        TranslationRequestServiceMock.Verify(
            service => service.CreateRequest(It.IsAny<TranslateAbleSubtitle>()), Times.Never);
        EmbeddedTrackServiceMock.Verify(
            service => service.PrepareTranslationSource(
                It.IsAny<MediaType>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessMedia_FlagOn_ExternalSourceStillWins()
    {
        // an external file matching the configured source language stays the
        // source — no ffmpeg run for the embedded twin
        var movie = await CreateMovieWithEmbeddedTrack();
        SetupStandardSettings();
        ExternalSubtitles(new Subtitles
        {
            Path = "/movies/test/test.movie.en.srt",
            FileName = "test.movie.en",
            Language = "en",
            Languages = ["en"],
            Caption = string.Empty,
            Format = ".srt"
        });
        EnableEmbeddedConsumption();

        var processed = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.True(processed);
        EmbeddedTrackServiceMock.Verify(
            service => service.PrepareTranslationSource(
                It.IsAny<MediaType>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        TranslationRequestServiceMock.Verify(
            service => service.CreateRequest(It.Is<TranslateAbleSubtitle>(request =>
                request.SubtitlePath == "/movies/test/test.movie.en.srt")),
            Times.Once);
    }

    [Fact]
    public async Task ProcessMedia_FlagOn_ExtractionFailure_SkipsMediaWithoutRequest()
    {
        var movie = await CreateMovieWithEmbeddedTrack();
        SetupStandardSettings();
        ExternalSubtitles();
        EnableEmbeddedConsumption();
        EmbeddedTrackServiceMock
            .Setup(service => service.PrepareTranslationSource(MediaType.Movie, 1, 2, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ffmpeg exited with code 1."));

        var processed = await Processor.ProcessMedia(movie, MediaType.Movie);

        Assert.False(processed);
        TranslationRequestServiceMock.Verify(
            service => service.CreateRequest(It.IsAny<TranslateAbleSubtitle>()), Times.Never);
        // the media hash is still written so the failed media is not retried
        // on every subsequent run
        Assert.False(string.IsNullOrEmpty(movie.MediaHash));
    }

    [Fact]
    public async Task ProcessMedia_FlagOn_GraphicTracksAndMarkersNeverBecomeCandidates()
    {
        var movie = await CreateMovieWithEmbeddedTrack(language: "zh", isTextBased: false, codec: "pgs");
        SetupStandardSettings();
        ExternalSubtitles();
        EnableEmbeddedConsumption();

        var processed = await Processor.ProcessMedia(movie, MediaType.Movie);

        // only the PGS row and the scan marker exist — nothing to translate
        Assert.False(processed);
        TranslationRequestServiceMock.Verify(
            service => service.CreateRequest(It.IsAny<TranslateAbleSubtitle>()), Times.Never);
    }
}
