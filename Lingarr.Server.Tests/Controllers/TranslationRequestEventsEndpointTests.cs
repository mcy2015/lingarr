using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Controllers;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models;
using Lingarr.Server.Models.TranslationRequests;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Controllers;

/// <summary>
/// P1-2 endpoint shape: GET /api/translationRequest/{id}/events returns the
/// request timeline oldest-first, with the typed event kind and provider.
/// </summary>
public class TranslationRequestEventsEndpointTests
{
    private readonly Mock<ITranslationRequestService> _requestService = new();
    private readonly Mock<ISubtitleConversionService> _conversionService = new();
    private readonly Mock<ITranslationRequestEventService> _eventService = new();
    private readonly TranslationRequestController _controller;

    public TranslationRequestEventsEndpointTests()
    {
        _controller = new TranslationRequestController(
            _requestService.Object,
            _conversionService.Object,
            _eventService.Object);
    }

    private static TranslationRequestEvent Event(
        TranslationStatus status,
        TranslationEventType? eventType = null,
        string? provider = null,
        string? message = null) => new()
    {
        TranslationRequestId = 2417,
        Status = status,
        EventType = eventType,
        Provider = provider,
        Message = message
    };

    [Fact]
    public async Task GetRequestEvents_ReturnsStoredOrderWithEventTypeAndProvider()
    {
        _eventService.Setup(e => e.RequestExists(2417)).ReturnsAsync(true);
        _eventService.Setup(e => e.GetEvents(2417)).ReturnsAsync(
        [
            Event(TranslationStatus.Pending, TranslationEventType.Created),
            Event(TranslationStatus.InProgress, TranslationEventType.ProviderFailed, "gemini", "429 · TooManyRequests"),
            Event(TranslationStatus.InProgress, TranslationEventType.Degraded, "openai", "gemini -> openai")
        ]);

        var result = await _controller.GetRequestEvents(2417);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var events = Assert.IsAssignableFrom<IEnumerable<TranslationRequestEventDetail>>(ok.Value).ToList();
        Assert.Equal(3, events.Count);
        Assert.Equal(TranslationEventType.Created, events[0].EventType);
        Assert.Equal("gemini", events[1].Provider);
        Assert.Equal("gemini -> openai", events[2].Message);
    }

    [Fact]
    public async Task GetRequestEvents_UnknownRequest_Is404()
    {
        _eventService.Setup(e => e.RequestExists(9999)).ReturnsAsync(false);

        var result = await _controller.GetRequestEvents(9999);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetRequestEvents_EmptyTimeline_ReturnsEmptyList()
    {
        _eventService.Setup(e => e.RequestExists(1)).ReturnsAsync(true);
        _eventService.Setup(e => e.GetEvents(1)).ReturnsAsync([]);

        var result = await _controller.GetRequestEvents(1);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var events = Assert.IsAssignableFrom<IEnumerable<TranslationRequestEventDetail>>(ok.Value).ToList();
        Assert.Empty(events);
    }
}
