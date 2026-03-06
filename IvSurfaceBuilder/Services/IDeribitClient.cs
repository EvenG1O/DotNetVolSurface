using IvSurfaceBuilder.Models;

namespace IvSurfaceBuilder.Services;


public interface IDeribitClient
{

    Task<decimal> GetIndexPriceAsync(string currency = "btc");

  Task<List<RawInstrument>> GetOptionInstrumentsAsync(string currency = "BTC");


    Task<List<IvPoint>> FetchIvPointsAsync(
     List<RawInstrument> instruments,
        decimal atmPrice,
        Action<int, int> onProgress);
}
