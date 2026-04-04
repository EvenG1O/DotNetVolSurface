using System.Collections.Concurrent;
using IvSurfaceBuilder.Models;

namespace IvSurfaceBuilder.Services;


/// Persistent WebSocket client to Deribit for real-time ticker subscriptions.

public interface IDeribitWsClient : IAsyncDisposable
{
  
    /// Connect to Deribit WebSocket endpoint.
   
    Task ConnectAsync(CancellationToken ct);


    /// Disconnect from Deribit WebSocket endpoint and clean up.

    Task DisconnectAsync(CancellationToken ct);


    /// Batch subscribe to ticker.{name}.raw channels for the given instruments.
    /// Sends up to 20 channels per public/subscribe call.

    Task SubscribeAsync(IEnumerable<string> instrumentNames, CancellationToken ct);


    /// Batch unsubscribe from ticker.{name}.raw channels.

    Task UnsubscribeAsync(IEnumerable<string> instrumentNames, CancellationToken ct);


    /// Latest ticker snapshot per instrument, continuously updated from the WS stream.

    ConcurrentDictionary<string, TickerSnapshot> LatestTickers { get; }


    /// Set of instrument names currently subscribed to.

    HashSet<string> ActiveSubscriptions { get; }


    /// Whether the WebSocket connection is currently open.

    bool IsConnected { get; }


    /// Total number of ticker notifications received since startup.

    long TickersReceived { get; }

  
    /// Fired when a new ticker update is received and stored.

    event Action<string, TickerSnapshot>? OnTickerUpdate;
}
