using Microsoft.AspNetCore.Mvc;
using IvSurfaceBuilder.Services;
using IvSurfaceBuilder.Models;
using System.Net.WebSockets;

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
        var status = new SocketStatus
        {
              
            DeribitConnected = _wsClient.IsConnected,
            SubscribedInstruments = _wsClient.ActiveSubscriptions.Count,
            MaxInstruments = 300,
            LastSurfaceUpdate = _streamService?.LastSurfaceUpdate,
            NextInstrumentRefresh = _streamService?.NextInstrumentRefresh,
            TickersReceived = _wsClient.TickersReceived


        };
     

        return Ok(status);
    }
}
