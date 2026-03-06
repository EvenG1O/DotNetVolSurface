using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using IvSurfaceBuilder.Models;
using IvSurfaceBuilder.Services;

namespace IvSurfaceBuilder.Controllers;


[ApiController]
[Route("api/[controller]")]
public class IvSurfaceController : ControllerBase
{
    private readonly IIvSurfaceService _service;
    private readonly ILogger<IvSurfaceController> _logger;

    public IvSurfaceController(IIvSurfaceService service, ILogger<IvSurfaceController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string currency = "BTC")
    {
        try
        {
            _logger.LogInformation("Fetching IV surface for currency: {Currency}", currency);
            var surface = await _service.BuildSurfaceAsync(currency.ToUpper());
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