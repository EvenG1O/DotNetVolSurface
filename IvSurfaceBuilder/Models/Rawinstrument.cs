namespace IvSurfaceBuilder.Models;

public record RawInstrument(
    string Name,
    DateTime Expiry,
    decimal Strike,
    string Type
);