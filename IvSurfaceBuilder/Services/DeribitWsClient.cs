using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using IvSurfaceBuilder.Models;

namespace IvSurfaceBuilder.Services;

/// <summary>
/// Persistent WebSocket client to Deribit with batch subscribe/unsubscribe,
/// automatic reconnection, and heartbeat keepalive.
/// </summary>
public class DeribitWsClient : IDeribitWsClient
{
    private readonly ILogger<DeribitWsClient> _logger;
    private readonly IConfiguration _config;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;
    private Task? _heartbeatTask;

    private int _nextRequestId = 1;
    private readonly object _sendLock = new();

    // --- Configuration ---
    private string DeribitWsUrl => _config.GetValue("VolSurfaceStream:DeribitWsUrl", "wss://www.deribit.com/ws/api/v2")!;
    private int SubscriptionBatchSize => _config.GetValue("VolSurfaceStream:SubscriptionBatchSize", 20);
    private int BatchDelayMs => _config.GetValue("VolSurfaceStream:BatchDelayMs", 200);
    private int HeartbeatIntervalSeconds => _config.GetValue("VolSurfaceStream:HeartbeatIntervalSeconds", 25);
    private int ReconnectMaxDelaySeconds => _config.GetValue("VolSurfaceStream:ReconnectMaxDelaySeconds", 30);

    // --- State ---
    public ConcurrentDictionary<string, TickerSnapshot> LatestTickers { get; } = new();
    public HashSet<string> ActiveSubscriptions { get; } = new();
    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public long TickersReceived => _tickersReceived;
    public event Action<string, TickerSnapshot>? OnTickerUpdate;

    private long _tickersReceived;
    private bool _disposed;

    public DeribitWsClient(ILogger<DeribitWsClient> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    // ──────────────────────────────────────────────
    // Connection
    // ──────────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken ct)
    {
        _ws?.Dispose();
        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(HeartbeatIntervalSeconds);

        _logger.LogInformation("Connecting to Deribit WebSocket at {Url}...", DeribitWsUrl);

        await _ws.ConnectAsync(new Uri(DeribitWsUrl), ct);

        _logger.LogInformation("Connected to Deribit WebSocket.");

        // Start the read loop
        _readLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readLoopTask = Task.Run(() => ReadLoopAsync(_readLoopCts.Token), _readLoopCts.Token);

        // Start heartbeat
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_readLoopCts.Token), _readLoopCts.Token);
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        _logger.LogInformation("Disconnecting from Deribit WebSocket...");
        await StopReadLoopAsync();

        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "No active clients", CancellationToken.None);
            }
            catch { /* Best effort */ }
        }

        lock (ActiveSubscriptions)
        {
            ActiveSubscriptions.Clear();
            LatestTickers.Clear();
        }

        _logger.LogInformation("Disconnected from Deribit WebSocket.");
    }


    // Subscribe / Unsubscribe (batched)


    public async Task SubscribeAsync(IEnumerable<string> instrumentNames, CancellationToken ct)
    {
        var names = instrumentNames.ToList();
        if (names.Count == 0) return;

        var channels = names.Select(n => $"ticker.{n}.100ms").ToList();

        _logger.LogInformation("Subscribing to {Count} instruments in batches of {BatchSize}...",
            names.Count, SubscriptionBatchSize);

        foreach (var batch in channels.Chunk(SubscriptionBatchSize))
        {
            var request = new
            {
                jsonrpc = "2.0",
                id = Interlocked.Increment(ref _nextRequestId),
                method = "public/subscribe",
                @params = new { channels = batch }
            };

            await SendJsonAsync(request, ct);

            _logger.LogDebug("Subscribed batch of {Count} channels (id={Id})",
                batch.Length, request.id);

            // Pause between batches to avoid overwhelming the connection
            if (batch.Length == SubscriptionBatchSize)
                await Task.Delay(BatchDelayMs, ct);
        }

        lock (ActiveSubscriptions)
        {
            foreach (var name in names)
                ActiveSubscriptions.Add(name);
        }

        _logger.LogInformation("Subscription complete. Total active: {Count}", ActiveSubscriptions.Count);
    }

    public async Task UnsubscribeAsync(IEnumerable<string> instrumentNames, CancellationToken ct)
    {
        var names = instrumentNames.ToList();
        if (names.Count == 0) return;

        var channels = names.Select(n => $"ticker.{n}.100ms").ToList();

        _logger.LogInformation("Unsubscribing from {Count} instruments...", names.Count);

        foreach (var batch in channels.Chunk(SubscriptionBatchSize))
        {
            var request = new
            {
                jsonrpc = "2.0",
                id = Interlocked.Increment(ref _nextRequestId),
                method = "public/unsubscribe",
                @params = new { channels = batch }
            };

            await SendJsonAsync(request, ct);

            if (batch.Length == SubscriptionBatchSize)
                await Task.Delay(BatchDelayMs, ct);
        }

        lock (ActiveSubscriptions)
        {
            foreach (var name in names)
            {
                ActiveSubscriptions.Remove(name);
                LatestTickers.TryRemove(name, out _);
            }
        }

        _logger.LogInformation("Unsubscription complete. Total active: {Count}", ActiveSubscriptions.Count);
    }


    // Read Loop — parses incoming subscription notifications
 

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[1024 * 64]; 

        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogWarning("Deribit closed the WebSocket connection.");
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(ms.ToArray());
                    ProcessMessage(json);
                }
            }
        }
        catch (OperationCanceledException) { /* Shutdown */ }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket read error. Connection may have dropped.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in Deribit WS read loop.");
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Subscription notifications have method = "subscription"
            if (root.TryGetProperty("method", out var methodProp) &&
                methodProp.GetString() == "subscription" &&
                root.TryGetProperty("params", out var paramsProp))
            {
                var channel = paramsProp.GetProperty("channel").GetString();
                var data = paramsProp.GetProperty("data");

                if (channel != null && channel.StartsWith("ticker.") && channel.EndsWith(".100ms"))
                {
                    var instrumentName = data.GetProperty("instrument_name").GetString()!;

                    var snapshot = new TickerSnapshot
                    {
                        InstrumentName = instrumentName,
                        MarkIv = GetDecimalOrDefault(data, "mark_iv"),
                        MarkPrice = GetDecimalOrDefault(data, "mark_price"),
                        UnderlyingPrice = GetDecimalOrDefault(data, "underlying_price"),
                        BidIv = GetDecimalOrDefault(data, "bid_iv"),
                        AskIv = GetDecimalOrDefault(data, "ask_iv"),
                        OpenInterest = GetDecimalOrDefault(data, "open_interest"),
                        Timestamp = data.TryGetProperty("timestamp", out var ts)
                            ? ts.GetInt64()
                            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };

                    LatestTickers[instrumentName] = snapshot;
                    Interlocked.Increment(ref _tickersReceived);

                    OnTickerUpdate?.Invoke(instrumentName, snapshot);
                }
            }
            else if (root.TryGetProperty("id", out _))
            {
                // This is a JSON-RPC response (subscribe/unsubscribe confirmation)
                // Log errors if any
                if (root.TryGetProperty("error", out var error))
                {
                    _logger.LogWarning("Deribit RPC error: {Error}",
                        error.GetRawText());
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse Deribit WS message.");
        }
    }


    // Heartbeat Loop
    

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                await Task.Delay(TimeSpan.FromSeconds(HeartbeatIntervalSeconds), ct);

                if (_ws?.State != WebSocketState.Open) break;

                var heartbeat = new
                {
                    jsonrpc = "2.0",
                    id = Interlocked.Increment(ref _nextRequestId),
                    method = "public/test",
                    @params = new { }
                };

                await SendJsonAsync(heartbeat, ct);
                _logger.LogDebug("Heartbeat sent.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Heartbeat loop error.");
        }
    }


    // Reconnection (called by VolSurfaceStreamService)



    /// Reconnect with exponential backoff and re-subscribe to all active channels.
    /// Called externally by VolSurfaceStreamService when it detects a disconnection.

    public async Task ReconnectAsync(CancellationToken ct)
    {
        var delay = 1;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Reconnecting to Deribit in {Delay}s...", delay);
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);

                // Clean up old connection
                await StopReadLoopAsync();

                await ConnectAsync(ct);

                // Re-subscribe to all active channels
                List<string> currentSubs;
                lock (ActiveSubscriptions)
                {
                    currentSubs = ActiveSubscriptions.ToList();
                }

                if (currentSubs.Count > 0)
                {
                    _logger.LogInformation("Re-subscribing to {Count} instruments after reconnect...",
                        currentSubs.Count);
                    await SubscribeAsync(currentSubs, ct);
                }

                _logger.LogInformation("Reconnection successful.");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Reconnection attempt failed.");
                delay = Math.Min(delay * 2, ReconnectMaxDelaySeconds);
            }
        }
    }





    private async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open)
        {
            _logger.LogWarning("Cannot send — WebSocket is not open.");
            return;
        }

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);


        await _sendSemaphore.WaitAsync(ct);
        try
        {
            await _ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct);
        }
        finally
        {
            _sendSemaphore.Release();
        }
    }

    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);

    private static decimal GetDecimalOrDefault(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number)
                return prop.GetDecimal();
        }
        return 0m;
    }

    private async Task StopReadLoopAsync()
    {
        if (_readLoopCts != null)
        {
            await _readLoopCts.CancelAsync();

            if (_readLoopTask != null)
            {
                try { await _readLoopTask; } catch { /* Expected */ }
            }
            if (_heartbeatTask != null)
            {
                try { await _heartbeatTask; } catch { /* Expected */ }
            }

            _readLoopCts.Dispose();
            _readLoopCts = null;
        }
    }


    // Disposal
  

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _logger.LogInformation("Disposing DeribitWsClient...");

        await StopReadLoopAsync();

        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Shutting down",
                    CancellationToken.None);
            }
            catch { /* Best effort */ }
        }

        _ws?.Dispose();
        _sendSemaphore.Dispose();

        _logger.LogInformation("DeribitWsClient disposed.");
    }
}
