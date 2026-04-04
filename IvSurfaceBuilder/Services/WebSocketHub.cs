using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using IvSurfaceBuilder.Models;

namespace IvSurfaceBuilder.Services;


/// Thread-safe manager for connected client WebSockets.
/// Handles fan-out broadcasting of IvSurface snapshots.

public class WebSocketHub
{
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private readonly ILogger<WebSocketHub> _logger;

    public WebSocketHub(ILogger<WebSocketHub> logger)
    {
        _logger = logger;
    }

    public int ConnectedClients => _clients.Count;

    public void AddClient(string connectionId, WebSocket socket)
    {
        _clients[connectionId] = socket;
        _logger.LogInformation("Client {Id} connected. Total: {Count}", connectionId, _clients.Count);
    }

    public void RemoveClient(string connectionId)
    {
        _clients.TryRemove(connectionId, out _);
        _logger.LogInformation("Client {Id} disconnected. Total: {Count}", connectionId, _clients.Count);
    }

  
    /// Serialize IvSurface once, then send to all connected clients concurrently.
    /// Dead clients are automatically removed on send failure.
  
    public async Task BroadcastAsync(IvSurface surface, CancellationToken ct)
    {
        if (_clients.IsEmpty) return;

        var json = JsonSerializer.Serialize(surface, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        var bytes = Encoding.UTF8.GetBytes(json);
        var segment = new ArraySegment<byte>(bytes);

        var deadClients = new List<string>();

        var sendTasks = _clients.Select(async kvp =>
        {
            try
            {
                if (kvp.Value.State == WebSocketState.Open)
                {
                    await kvp.Value.SendAsync(
                        segment,
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        ct);
                }
                else
                {
                    lock (deadClients) { deadClients.Add(kvp.Key); }
                }
            }
            catch (Exception)
            {
                lock (deadClients) { deadClients.Add(kvp.Key); }
            }
        });

        await Task.WhenAll(sendTasks);

        // Clean up dead clients
        foreach (var id in deadClients)
        {
            RemoveClient(id);
        }

        if (deadClients.Count > 0)
        {
            _logger.LogDebug("Removed {Count} dead clients after broadcast.", deadClients.Count);
        }
    }
}
