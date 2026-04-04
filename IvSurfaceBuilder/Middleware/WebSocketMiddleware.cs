using System.Net.WebSockets;
using IvSurfaceBuilder.Services;

namespace IvSurfaceBuilder.Middleware;

/// Middleware that upgrades HTTP requests to /ws/volsurface to WebSocket connections
/// and registers them with the WebSocketHub for receiving surface broadcasts.

public class WebSocketMiddleware
{
    private readonly RequestDelegate _next;
    private readonly WebSocketHub _hub;
    private readonly ILogger<WebSocketMiddleware> _logger;

    public WebSocketMiddleware(RequestDelegate next, WebSocketHub hub, ILogger<WebSocketMiddleware> logger)
    {
        _next = next;
        _hub = hub;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path != "/ws/volsurface")
        {
            await _next(context);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Expected a WebSocket request.");
            return;
        }

        var connectionId = Guid.NewGuid().ToString("N");
        var ws = await context.WebSockets.AcceptWebSocketAsync();

        _logger.LogInformation("WebSocket client {Id} connected from {Ip}",
            connectionId,
            context.Connection.RemoteIpAddress);

        _hub.AddClient(connectionId, ws);

        try
        {
            // Keep the connection alive by reading messages (close frames, pings)
            await KeepAliveAsync(ws, connectionId, context.RequestAborted);
        }
        finally
        {
            _hub.RemoveClient(connectionId);

            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await ws.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Server closing",
                        CancellationToken.None);
                }
                catch { /* Best effort close */ }
            }
        }
    }

  /// Reads from the client WS until it disconnects.
    /// We don't expect any meaningful messages from the client — this just
    /// keeps the middleware alive and detects disconnection.
 
    private async Task KeepAliveAsync(WebSocket ws, string connectionId, CancellationToken ct)
    {
        var buffer = new byte[1024];

        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Client {Id} sent close frame.", connectionId);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket error for client {Id}.", connectionId);
        }
    }
}
