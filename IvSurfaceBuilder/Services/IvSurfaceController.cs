using IvSurfaceBuilder.Models;

namespace IvSurfaceBuilder.Services;

public class IvSurfaceService : IIvSurfaceService
{
    private readonly IDeribitClient _client;
    private readonly InstrumentFilter _filter;

    public IvSurfaceService(IDeribitClient client, InstrumentFilter filter)
    {
        _client = client;
        _filter = filter;
    }

    public async Task<IvSurface> BuildSurfaceAsync(string currency = "BTC")
    {
        try
        {
            var atmPrice = await _client.GetIndexPriceAsync(currency.ToLower());
            var allInstruments = await _client.GetOptionInstrumentsAsync(currency);
            var filtered = _filter.Filter(allInstruments, atmPrice);

            var ivPoints = await _client.FetchIvPointsAsync(
                filtered,
                atmPrice,
                (current, total) => System.Diagnostics.Debug.WriteLine($"  Progress: {current}/{total}")
            );

            return new IvSurface
            {
                AtmPrice = atmPrice,
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
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to build IV surface for {currency}", ex);
        }
    }
}