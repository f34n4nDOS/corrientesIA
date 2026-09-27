using CorrientesIA.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace CorrientesIA.Api.Controllers;

[ApiController]
[Route("api/web-search-test")]
public class WebSearchTestController : ControllerBase
{
    private readonly WebSearchService _webSearch;

    public WebSearchTestController(WebSearchService webSearch)
    {
        _webSearch = webSearch;
    }

    [HttpGet]
    public async Task<IActionResult> Buscar(
        [FromQuery] string q,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest("La consulta no puede estar vacia.");

        var resultados =
            await _webSearch.BuscarAsync(
                q,
                cancellationToken);

        return Ok(resultados);
    }
}