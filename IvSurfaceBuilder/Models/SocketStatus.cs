namespace IvSurfaceBuilder.Models;


public  record SocketStatus
{
    public bool DeribitConnected { get; init; }
    public int SubscribedInstruments { get; init; }
    public int MaxInstruments { get; init; }
    public DateTime? LastSurfaceUpdate { get; init; }
    public DateTime? NextInstrumentRefresh { get; init; }
    public long TickersReceived { get; init; }

}