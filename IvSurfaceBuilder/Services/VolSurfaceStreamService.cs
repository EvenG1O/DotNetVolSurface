using IvSurfaceBuilder.Models;

namespace IvSurfaceBuilder.Services;


/// BackgroundService that orchestrates the real-time vol surface streaming pipeline:
/// 1. Bootstrap: fetch instruments (HTTP), filter+cap to ≤300
/// 2. Connect DeribitWsClient and batch-subscribe to ticker channels
/// 3. Every 5s: rebuild IvSurface from latest ticker data, broadcast to clients
/// 4. Every 15m: refresh instrument list, diff-based unsub/sub

public class VolSurfaceStreamService : BackgroundService
{
    private readonly IDeribitClient _httpClient;
    private readonly IDeribitWsClient _wsClient;
    private readonly InstrumentFilter _filter;
    private readonly WebSocketHub _hub;
    private readonly ILogger<VolSurfaceStreamService> _logger;
    private readonly IConfiguration _config;

    // Current state
    private Dictionary<string, List<RawInstrument>> _currentInstruments = new();
    private Dictionary<string, decimal> _currentAtmPrices = new();
    private DateTime _lastSurfaceUpdate = DateTime.MinValue;
    private DateTime _lastInstrumentRefresh = DateTime.MinValue;
    private DateTime _nextInstrumentRefresh = DateTime.MinValue;

    // Configuration
    private int RebuildIntervalSeconds => _config.GetValue("VolSurfaceStream:RebuildIntervalSeconds", 5);
    private int InstrumentRefreshMinutes => _config.GetValue("VolSurfaceStream:InstrumentRefreshMinutes", 15);
    private int MaxInstruments => _config.GetValue("VolSurfaceStream:MaxInstruments", 300);
    private string[] Currencies => _config.GetValue("VolSurfaceStream:Currencies", "BTC,ETH")!.Split(',');

    // Observability
    public DateTime LastSurfaceUpdate => _lastSurfaceUpdate;
    public DateTime NextInstrumentRefresh => _nextInstrumentRefresh;

    public VolSurfaceStreamService(
        IDeribitClient httpClient,
        IDeribitWsClient wsClient,
        InstrumentFilter filter,
        WebSocketHub hub,
        ILogger<VolSurfaceStreamService> logger,
        IConfiguration config)
    {
        _httpClient = httpClient;
        _wsClient = wsClient;
        _filter = filter;
        _hub = hub;
        _logger = logger;
        _config = config;
    }

    private bool _isStreaming = false;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("VolSurfaceStreamService starting...");

        // Short delay to let the app finish startup
        await Task.Delay(2000, stoppingToken);

        try
        {
            using var rebuildTimer = new PeriodicTimer(TimeSpan.FromSeconds(RebuildIntervalSeconds));

            while (await rebuildTimer.WaitForNextTickAsync(stoppingToken))
            {
                bool hasClients = _hub.ConnectedClients > 0;

                if (hasClients && !_isStreaming)
                {
                    _logger.LogInformation("Clients connected. Starting Deribit stream...");
                    
                    await BootstrapInstrumentsAsync(stoppingToken);
                    await _wsClient.ConnectAsync(stoppingToken);

                    var instrumentNames = _currentInstruments.Values.SelectMany(x => x).Select(i => i.Name).ToList();
                    await _wsClient.SubscribeAsync(instrumentNames, stoppingToken);

                    _logger.LogInformation("Streaming started. Rebuilds every {Interval}s, instrument refresh every {Refresh}m.", 
                        RebuildIntervalSeconds, InstrumentRefreshMinutes);
                    
                    _nextInstrumentRefresh = DateTime.UtcNow.AddMinutes(InstrumentRefreshMinutes);
                    _isStreaming = true;
                }
                else if (!hasClients && _isStreaming)
                {
                    _logger.LogInformation("No clients connected. Halting Deribit stream to save resources...");
                    await _wsClient.DisconnectAsync(stoppingToken);
                    _isStreaming = false;
                }

                if (_isStreaming)
                {
                    if (!_wsClient.IsConnected)
                    {
                        _logger.LogWarning("Deribit WS disconnected. Attempting reconnect...");
                        await ((DeribitWsClient)_wsClient).ReconnectAsync(stoppingToken);
                        continue;
                    }

                    await RebuildAndBroadcastSurfaceAsync(stoppingToken);

                    if (DateTime.UtcNow >= _nextInstrumentRefresh)
                    {
                        await RefreshInstrumentsAsync(stoppingToken);
                        _nextInstrumentRefresh = DateTime.UtcNow.AddMinutes(InstrumentRefreshMinutes);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("VolSurfaceStreamService stopping (cancellation requested).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VolSurfaceStreamService encountered a fatal error.");
        }
    }


    // Bootstrap — one-time instrument fetch + filter


    private async Task BootstrapInstrumentsAsync(CancellationToken ct)
    {
        _logger.LogInformation("Bootstrapping instruments for {Currencies}...", string.Join(", ", Currencies));
        _currentInstruments.Clear();
        _currentAtmPrices.Clear();

        foreach (var currency in Currencies)
        {
            var atmPrice = await _httpClient.GetIndexPriceAsync(currency.ToLower());
            var allInstruments = await _httpClient.GetOptionInstrumentsAsync(currency);
            var filtered = _filter.FilterAndCap(allInstruments, atmPrice, MaxInstruments / Currencies.Length);

            _currentInstruments[currency] = filtered;
            _currentAtmPrices[currency] = atmPrice;

            _logger.LogInformation(
                "Bootstrap complete for {Currency}. ATM={AtmPrice}, Total instruments={Total}, Filtered+Capped={Filtered}",
                currency, atmPrice, allInstruments.Count, filtered.Count);
        }

        _lastInstrumentRefresh = DateTime.UtcNow;
    }


    // Surface Rebuild — read tickers, build IvSurface, broadcast


    private async Task RebuildAndBroadcastSurfaceAsync(CancellationToken ct)
    {
        try
        {
            var tickers = _wsClient.LatestTickers;

            if (tickers.IsEmpty)
            {
                _logger.LogDebug("No ticker data yet, skipping surface rebuild.");
                return;
            }

            foreach (var currency in Currencies)
            {
                if (!_currentInstruments.TryGetValue(currency, out var currentInsts) || currentInsts.Count == 0)
                    continue;

                var currentAtmPrice = _currentAtmPrices.TryGetValue(currency, out var atm) ? atm : 0m;

                // Build a lookup of instrument name → RawInstrument
                var instrumentLookup = currentInsts.ToDictionary(i => i.Name, i => i);

                var ivPoints = new List<IvPoint>();

                foreach (var kvp in tickers)
                {
                    if (!instrumentLookup.TryGetValue(kvp.Key, out var inst))
                        continue;

                    var ticker = kvp.Value;

                    if (ticker.MarkIv <= 0)
                        continue;

                    var moneyness = currentAtmPrice > 0
                        ? Math.Round(inst.Strike / currentAtmPrice, 4)
                        : 0m;

                    ivPoints.Add(new IvPoint(
                        inst.Expiry,
                        inst.Strike,
                        inst.Type,
                        ticker.MarkIv,
                        moneyness));
                }

                if (ivPoints.Count == 0)
                {
                    _logger.LogDebug("No valid IV points from tickers for {Currency}, skipping broadcast.", currency);
                    continue;
                }

                // Use the underlying price from any ticker for this currency
                var latestAtm = tickers.Values
                    .Where(t => instrumentLookup.ContainsKey(t.InstrumentName))
                    .Where(t => t.UnderlyingPrice > 0)
                    .OrderByDescending(t => t.Timestamp)
                    .Select(t => t.UnderlyingPrice)
                    .FirstOrDefault(currentAtmPrice);

                var surface = new IvSurface
                {
                    AtmPrice = latestAtm,
                    Timestamp = DateTime.UtcNow,
                    Currency = currency,
                    Expiries = ivPoints
                        .GroupBy(x => x.Expiry)
                        .OrderBy(x => x.Key)
                        .Select(g => new IvExpiry
                        {
                            Expiry = g.Key,
                            Points = g.OrderBy(x => x.Strike).ToList()
                        })
                        .ToList()
                };

                await _hub.BroadcastAsync(surface, ct);
                
                _logger.LogDebug(
                    "Surface broadcast for {Currency}: {PointCount} points, {ExpiryCount} expiries, {ClientCount} clients. ATM={Atm}",
                    currency, ivPoints.Count, surface.Expiries.Count, _hub.ConnectedClients, latestAtm);
            }

            _lastSurfaceUpdate = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error rebuilding/broadcasting surface.");
        }
    }


    // Instrument Refresh — diff-based unsub/sub


    private async Task RefreshInstrumentsAsync(CancellationToken ct)
    {
        _logger.LogInformation("Refreshing instruments (15-minute interval)...");

        try
        {
            var allOldNames = _currentInstruments.Values.SelectMany(x => x).Select(i => i.Name).ToHashSet();
            var allNewNames = new HashSet<string>();

            foreach (var currency in Currencies)
            {
                var newAtmPrice = await _httpClient.GetIndexPriceAsync(currency.ToLower());
                var allInstruments = await _httpClient.GetOptionInstrumentsAsync(currency);
                var newFiltered = _filter.FilterAndCap(allInstruments, newAtmPrice, MaxInstruments / Currencies.Length);

                _currentInstruments[currency] = newFiltered;
                _currentAtmPrices[currency] = newAtmPrice;

                foreach (var inst in newFiltered)
                {
                    allNewNames.Add(inst.Name);
                }
            }

            var removed = allOldNames.Except(allNewNames).ToList();
            var added = allNewNames.Except(allOldNames).ToList();

            _logger.LogInformation(
                "Instrument refresh: {Removed} removed, {Added} added, {Unchanged} unchanged.",
                removed.Count, added.Count, allOldNames.Intersect(allNewNames).Count());

            // Unsubscribe removed instruments
            if (removed.Count > 0)
            {
                await _wsClient.UnsubscribeAsync(removed, ct);
            }

            // Subscribe to new instruments
            if (added.Count > 0)
            {
                await _wsClient.SubscribeAsync(added, ct);
            }

            _lastInstrumentRefresh = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh instruments. Will retry next cycle.");
        }
    }
}
