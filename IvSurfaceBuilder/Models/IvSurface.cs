namespace IvSurfaceBuilder.Models;

public class IvSurface
{
    public decimal AtmPrice { get; set; }
    public DateTime Timestamp { get; set; }
    public string Currency { get; set; } = "BTC";
    public List<IvExpiry> Expiries { get; set; } = new();
}

public class IvExpiry
{
    public DateTime Expiry { get; set; }
    public List<IvPoint> Points { get; set; } = new();
}

