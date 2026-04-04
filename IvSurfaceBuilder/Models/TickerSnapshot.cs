namespace IvSurfaceBuilder.Models;

/// Parsed Deribit ticker notification data from the ticker.{instrument}.raw channel.

public record TickerSnapshot
{
    public required string InstrumentName { get; init; }
    public decimal MarkIv { get; init; }
    public decimal MarkPrice { get; init; }
    public decimal UnderlyingPrice { get; init; }
    public decimal BidIv { get; init; }
    public decimal AskIv { get; init; }
    public decimal OpenInterest { get; init; }
    public long Timestamp { get; init; }
}
