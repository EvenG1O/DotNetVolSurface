using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using IvSurfaceBuilder.Models;
using IvSurfaceBuilder.Services;
using Microsoft.Extensions.Caching.Memory;

namespace IvSurfaceBuilder.Controllers;


[ApiController]
[Route("api/[controller]")]
public class IvSurfaceController : ControllerBase
{
    private readonly IIvSurfaceService _service;
    private readonly ILogger<IvSurfaceController> _logger;
    private readonly IMemoryCache _cache;
    private const int CacheMinutes = 5;

    public IvSurfaceController(IIvSurfaceService service, ILogger<IvSurfaceController> logger,IMemoryCache cache )
    {
        _service = service;
        _logger = logger;
        _cache = cache;
        

    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string currency = "BTC")
    {
        try
        {
          var cacheKey = $"iv_surface_{currency.ToUpper()}";

            if(_cache.TryGetValue(cacheKey, out IvSurface? cachedSurface) && cachedSurface != null)
            {
                _logger.LogInformation("Cache hit for IV surface of {Currency}", currency);
                return Ok(cachedSurface);
            }

            _logger.LogInformation("Cache miss for IV surface of {Currency}. Building new surface.", currency);

            var  surface = await _service.BuildSurfaceAsync(currency);

            _cache.Set(cacheKey, surface, TimeSpan.FromMinutes(CacheMinutes));
            return Ok(surface);



        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Invalid operation when building IV surface for {Currency}", currency);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error building IV surface for {Currency}", currency);
            return StatusCode(500, new { error = "An unexpected error occurred", details = ex.Message });
        }
    }
}