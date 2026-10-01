using System;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Api.Models.LiveTvDtos;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Tuner signal readings of live TV channels, so clients can warn about weak channels and show the signal while
/// watching (Finly).
/// </summary>
[Route("LiveTv/ChannelSignal")]
[Authorize(Policy = Policies.LiveTvAccess)]
public class ChannelSignalController : BaseJellyfinApiController
{
    private readonly IChannelSignalService _signals;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelSignalController"/> class.
    /// </summary>
    /// <param name="signals">The channel signal service.</param>
    public ChannelSignalController(IChannelSignalService signals)
    {
        _signals = signals;
    }

    /// <summary>
    /// Gets the latest signal reading of every channel that has one.
    /// </summary>
    /// <response code="200">The readings, by channel id.</response>
    /// <returns>The readings and the quality under which a channel is weak.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ChannelSignalsDto>> GetChannelSignals()
    {
        var signals = await _signals.GetChannelSignalsAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return new ChannelSignalsDto
        {
            WeakQualityThreshold = _signals.WeakQualityThreshold,
            Channels = signals.ToDictionary(s => s.Key.ToString("N", CultureInfo.InvariantCulture), s => s.Value)
        };
    }

    /// <summary>
    /// Gets a channel's latest signal reading, live and current when a tuner is on the channel or its multiplex.
    /// </summary>
    /// <param name="channelId">The channel id.</param>
    /// <response code="200">The reading.</response>
    /// <response code="404">The channel isn't a tuner channel or has no reading.</response>
    /// <returns>The reading.</returns>
    [HttpGet("{channelId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChannelSignal>> GetChannelSignal([FromRoute, Required] Guid channelId)
    {
        var signal = await _signals.GetChannelSignalAsync(channelId, HttpContext.RequestAborted).ConfigureAwait(false);
        return signal is null ? NotFound() : signal;
    }
}
