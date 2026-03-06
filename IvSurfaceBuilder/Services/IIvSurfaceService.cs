using IvSurfaceBuilder.Models;

namespace IvSurfaceBuilder.Services;

public interface IIvSurfaceService
{

    Task<IvSurface> BuildSurfaceAsync(string currency = "BTC");
}
