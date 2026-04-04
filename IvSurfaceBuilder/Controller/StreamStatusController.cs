using Microsoft.AspNetCore.Mvc;
using IvSurfaceBuilder.Services;

namespace IvSurfaceBuilder.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StreamStatusController : ControllerBase
{
    private readonly IDeribitWsClient _wsClient;
    private readonly WebSocketHub _hub;
    private readonly VolSurfaceStreamService _streamService;

    public StreamStatusController(
        IDeribitWsClient wsClient,
        WebSocketHub hub,
        IEnumerable<IHostedService> hostedServices)
    {
        _wsClient = wsClient;
        _hub = hub;
        _streamService = hostedServices.OfType<VolSurfaceStreamService>().FirstOrDefault()!;
    }

    [HttpGet]
    public IActionResult GetStatus()
    {
        var status = new
        {
            deribitConnected = _wsClient.IsConnected,
            subscribedInstruments = _wsClient.ActiveSubscriptions.Count,
            maxInstruments = 300, // as configured in appsettings
            connectedClients = _hub.ConnectedClients,
            lastSurfaceUpdate = _streamService?.LastSurfaceUpdate,
            nextInstrumentRefresh = _streamService?.NextInstrumentRefresh,
            tickersReceived = _wsClient.TickersReceived
        };

        return Ok(status);
    }
}
