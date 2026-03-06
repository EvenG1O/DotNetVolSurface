namespace IvSurfaceBuilder.Models;

public record IvPoint(
    DateTime Expiry,
    Decimal Strike,
    string Type,
    decimal Iv,
    decimal Moneyness
);