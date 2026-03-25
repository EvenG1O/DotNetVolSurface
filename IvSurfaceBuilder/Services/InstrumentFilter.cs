using IvSurfaceBuilder.Models;


namespace IvSurfaceBuilder.Services;

public class InstrumentFilter 
{
    private const int MaxExpiries = 6;
    private const decimal FilterPct = 0.15m;
    private const decimal AtmTolerance = 500m;

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

        return instruments
            .Where(x => nearestExpiries.Contains(x.Expiry))
            .Where(x => x.Strike >= lower && x.Strike <= upper)
            .Where(x =>
                (x.Strike < atmPrice - AtmTolerance && x.Type == "put") ||
                (x.Strike > atmPrice + AtmTolerance && x.Type == "call") ||
                (Math.Abs(x.Strike - atmPrice) <= AtmTolerance))
            .OrderBy(x => x.Expiry)
            .ThenBy(x => x.Strike)
            .ToList();
    }
}