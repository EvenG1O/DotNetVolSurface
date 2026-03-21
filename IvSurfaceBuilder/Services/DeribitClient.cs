using System.Collections.Concurrent;
using System.Text.Json;
using IvSurfaceBuilder.Models;
using Microsoft.Extensions.Caching.Memory;

namespace IvSurfaceBuilder.Services;

public class DeribitClient : IDeribitClient
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;

    private const int BatchSize = 10;
    private const int BatchDelayMs = 750;

    private const string IndexPriceCacheKey = "index_price_";
    private const string InstrumentsCacheKey = "instruments_";
    private const string IvPointCacheKey = "iv_point_";

    private static readonly TimeSpan IndexPriceCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InstrumentsCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IvPointCacheDuration = TimeSpan.FromMinutes(5);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _instrumentSemaphores = new();

    public DeribitClient(HttpClient http, IMemoryCache cache)
    {
        _http = http;
        _cache = cache;
    }

    private static SemaphoreSlim GetSemaphore(string instrumentName)
    {
        return _instrumentSemaphores.GetOrAdd(instrumentName, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<decimal> GetIndexPriceAsync(string currency = "btc")
    {
        
        var cacheKey = IndexPriceCacheKey + currency.ToLower();

        
        if (_cache.TryGetValue(cacheKey, out decimal cachedPrice))
        {
            System.Diagnostics.Debug.WriteLine($"Cache hit — index price {currency}");
            return cachedPrice;
        }

        try
        {
            var url = $"https://www.deribit.com/api/v2/public/get_index_price?index_name={currency}_usd";
            var response = await _http.GetStringAsync(url);
            var doc = JsonDocument.Parse(response);
            var price = doc.RootElement
                .GetProperty("result")
                .GetProperty("index_price")
                .GetDecimal();

          
            _cache.Set(cacheKey, price, IndexPriceCacheDuration);
            System.Diagnostics.Debug.WriteLine($"Cache set — index price {currency} = {price}");

            return price;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to fetch index price for {currency}", ex);
        }
    }

    public async Task<List<RawInstrument>> GetOptionInstrumentsAsync(string currency = "BTC")
    {
       
        var cacheKey = InstrumentsCacheKey + currency.ToUpper();

       
        if (_cache.TryGetValue(cacheKey, out List<RawInstrument>? cachedInstruments)
            && cachedInstruments != null)
        {
            System.Diagnostics.Debug.WriteLine($"Cache hit — instruments {currency}");
            return cachedInstruments;
        }

        try
        {
            var url = $"https://www.deribit.com/api/v2/public/get_instruments?currency={currency}&kind=option&expired=false";
            var response = await _http.GetStringAsync(url);
            var doc = JsonDocument.Parse(response);
            var results = doc.RootElement.GetProperty("result");

            var instruments = new List<RawInstrument>();

            foreach (var item in results.EnumerateArray())
            {
                var name = item.GetProperty("instrument_name").GetString()!;
                var expTimestamp = item.GetProperty("expiration_timestamp").GetInt64();
                var strike = item.GetProperty("strike").GetDecimal();
                var optionType = item.GetProperty("option_type").GetString()!;
                var expiry = DateTimeOffset.FromUnixTimeMilliseconds(expTimestamp).UtcDateTime;

                instruments.Add(new RawInstrument(name, expiry, strike, optionType));
            }

      
            _cache.Set(cacheKey, instruments, InstrumentsCacheDuration);
            System.Diagnostics.Debug.WriteLine($"Cache set — {instruments.Count} instruments for {currency}");

            return instruments;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to fetch option instruments for {currency}", ex);
        }
    }

    public async Task<List<IvPoint>> FetchIvPointsAsync(
        List<RawInstrument> instruments,
        decimal atmPrice,
        Action<int, int> onProgress)
    {
        ArgumentNullException.ThrowIfNull(instruments);
        ArgumentNullException.ThrowIfNull(onProgress);

        var ivPoints = new List<IvPoint>();

        for (int i = 0; i < instruments.Count; i += BatchSize)
        {
            var batch = instruments.Skip(i).Take(BatchSize);
            var batchTasks = batch.Select(inst => FetchSingleIvPointAsync(inst, atmPrice)).ToList();
            var batchResults = await Task.WhenAll(batchTasks);
            ivPoints.AddRange(batchResults.Where(p => p != null)!);

            onProgress(Math.Min(i + BatchSize, instruments.Count), instruments.Count);

            if (i + BatchSize < instruments.Count)
                await Task.Delay(BatchDelayMs);
        }

        return ivPoints;
    }

    private async Task<IvPoint?> FetchSingleIvPointAsync(RawInstrument inst, decimal atmPrice)
    {
        var cacheKey = IvPointCacheKey + inst.Name;
        var semaphore = GetSemaphore(inst.Name);

        if (_cache.TryGetValue(cacheKey, out IvPoint? cachedPoint))
        {
            System.Diagnostics.Debug.WriteLine($"Cache hit — {inst.Name}");
            return cachedPoint;
        }

        await semaphore.WaitAsync();
        try
        {
            if (_cache.TryGetValue(cacheKey, out cachedPoint))
            {
                System.Diagnostics.Debug.WriteLine($"Cache hit (after wait) — {inst.Name}");
                return cachedPoint;
            }

            var url = $"https://www.deribit.com/api/v2/public/get_order_book?instrument_name={inst.Name}";
            var response = await _http.GetStringAsync(url);
            var doc = JsonDocument.Parse(response);

            var markIv = doc.RootElement
                .GetProperty("result")
                .GetProperty("mark_iv")
                .GetDecimal();

            if (markIv > 0)
            {
                var moneyness = Math.Round(inst.Strike / atmPrice, 4);
                var ivPoint = new IvPoint(inst.Expiry, inst.Strike, inst.Type, markIv, moneyness);
                _cache.Set(cacheKey, ivPoint, IvPointCacheDuration);
                System.Diagnostics.Debug.WriteLine($"Cache set — {inst.Name}");
                return ivPoint;
            }

            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"✘ {inst.Name} → {ex.Message}");
            return null;
        }
        finally
        {
            semaphore.Release();
        }
    }
}