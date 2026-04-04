using IvSurfaceBuilder.Models;


namespace IvSurfaceBuilder.Services;

public class InstrumentFilter 
{
    private const int MaxExpiries = 6;
    private const decimal FilterPct = 0.15m;
    private const decimal AtmTolerancePct = 0.01m; 

    public List<RawInstrument> Filter(List<RawInstrument> instruments, decimal atmPrice)
    {
        var nearestExpiries = instruments
            .Select(x => x.Expiry)
            .Distinct()
            .OrderBy(x => x)
            .Take(MaxExpiries)
            .ToHashSet();

        var lower = atmPrice * (1 - FilterPct);
        var upper = atmPrice * (1 + FilterPct);
        var atmTolerance = atmPrice * AtmTolerancePct;

        return instruments
            .Where(x => nearestExpiries.Contains(x.Expiry))
            .Where(x => x.Strike >= lower && x.Strike <= upper)
            .Where(x =>
                (x.Strike < atmPrice - atmTolerance && x.Type == "put") ||
                (x.Strike > atmPrice + atmTolerance && x.Type == "call") ||
                (Math.Abs(x.Strike - atmPrice) <= atmTolerance))
            .OrderBy(x => x.Expiry)
            .ThenBy(x => x.Strike)
            .ToList();
    }

    /// Filters instruments for liquidity using Filter(), then enforces a hard cap
    /// by keeping strikes closest to ATM (most liquid) within each expiry.
    /// Used by the WebSocket streaming service to stay under Deribit's subscription limit (500).
  
    public List<RawInstrument> FilterAndCap(List<RawInstrument> instruments, decimal atmPrice, int maxInstruments = 300)
    {
        var filtered = Filter(instruments, atmPrice);

        if (filtered.Count <= maxInstruments)
            return filtered;

        // Over the cap — distribute evenly across expiries, keep closest to ATM
        var expiryCount = filtered.Select(x => x.Expiry).Distinct().Count();
        var perExpiry = Math.Max(1, maxInstruments / expiryCount);

        return filtered
            .GroupBy(x => x.Expiry)
            .OrderBy(g => g.Key)
            .SelectMany(g => g
                .OrderBy(x => Math.Abs(x.Strike - atmPrice))
                .Take(perExpiry))
            .OrderBy(x => x.Expiry)
            .ThenBy(x => x.Strike)
            .Take(maxInstruments)
            .ToList();
    }
}