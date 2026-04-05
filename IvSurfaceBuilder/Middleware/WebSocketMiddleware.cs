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
    private readonly string _allowedOrigin;

    public WebSocketMiddleware(RequestDelegate next, WebSocketHub hub, ILogger<WebSocketMiddleware> logger, IConfiguration configuration)
    {
        _next = next;
        _hub = hub;
        _logger = logger;
        
        _allowedOrigin = configuration["AllowedOrigin"] ?? "https://eveng1o.github.io";
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

        var origin = context.Request.Headers.Origin.ToString();
        var isAllowed = !string.IsNullOrEmpty(origin) && 
                        origin.Equals(_allowedOrigin, StringComparison.OrdinalIgnoreCase);

        if (!isAllowed)
        {
            _logger.LogWarning("WebSocket connection rejected from unauthorized origin: {Origin}", origin);
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Origin not allowed");
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
