using System.Text.Json;
using IvSurfaceBuilder.Models;

namespace IvSurfaceBuilder.Services;


public class DeribitClient : IDeribitClient
{
    private readonly HttpClient _http;
    private const int BatchSize = 10;
    private const int BatchDelayMs = 750;

    public DeribitClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<decimal> GetIndexPriceAsync(string currency = "btc")
    {
        try
        {
            var url = $"https://www.deribit.com/api/v2/public/get_index_price?index_name={currency}_usd";
            var response = await _http.GetStringAsync(url);
            var doc = JsonDocument.Parse(response);
            return doc.RootElement
                .GetProperty("result")
                .GetProperty("index_price")
                .GetDecimal();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to fetch index price for {currency}", ex);
        }
    }


    public async Task<List<RawInstrument>> GetOptionInstrumentsAsync(string currency = "BTC")
    {
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

            foreach (var inst in batch)
            {
                try
                {
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
                        ivPoints.Add(new IvPoint(inst.Expiry, inst.Strike, inst.Type, markIv, moneyness));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"✘ {inst.Name} → {ex.Message}");
                }
            }

            onProgress(Math.Min(i + BatchSize, instruments.Count), instruments.Count);

            if (i + BatchSize < instruments.Count)
                await Task.Delay(BatchDelayMs);
        }

        return ivPoints;
    }
}