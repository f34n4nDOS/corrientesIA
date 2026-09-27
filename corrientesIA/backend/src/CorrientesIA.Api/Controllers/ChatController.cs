using CorrientesIA.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace CorrientesIA.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly InferenceService _inference;
    private readonly GroundingService _grounding;
    private readonly WebSearchService _webSearch;

    public ChatController(
        InferenceService inference,
        GroundingService grounding,
        WebSearchService webSearch)
    {
        _inference = inference;
        _grounding = grounding;
        _webSearch = webSearch;
    }

    public record ChatRequest(string Mensaje);
    public record ChatResponse(string Respuesta);

    [HttpPost]
    public async Task<ActionResult<ChatResponse>> Post(
        [FromBody] ChatRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Mensaje))
        {
            return BadRequest("El mensaje no puede estar vacio.");
        }

        // 1. Buscar primero en la base local verificada.
        var datoVerificado =
            await _grounding.BuscarPorPalabrasClaveAsync(
                request.Mensaje);

        if (datoVerificado is not null)
        {
            return Ok(new ChatResponse(datoVerificado));
        }

        // 2. Si no existe una coincidencia directa,
        //    buscar en el corpus propio.
        var datoCorpus =
            await _grounding.BuscarEnCorpusAsync(
                request.Mensaje);

        if (datoCorpus is not null)
        {
            return Ok(new ChatResponse(datoCorpus));
        }

        // 3. Ultimo recurso: búsqueda web.
        var resultadosWeb =
            await _webSearch.BuscarAsync(
                request.Mensaje,
                cancellationToken);

        var mejorResultado =
            resultadosWeb.FirstOrDefault();
    Console.WriteLine($"WEB RESULTADO: {mejorResultado?.Title} | {mejorResultado?.Content}");
        if (mejorResultado is not null &&
            !string.IsNullOrWhiteSpace(mejorResultado.Content) &&
            !string.IsNullOrWhiteSpace(mejorResultado.Url))
        {
                var contenido = mejorResultado.Content.Trim();
                contenido = contenido.Replace(
    "¿Cuál es la capital de Francia?",
    "",
    StringComparison.OrdinalIgnoreCase
).Trim();
if (contenido.EndsWith("..."))
{
    contenido = contenido[..^3].Trim();
}

contenido = contenido.Replace("La. capital", "La capital");

var primeraOracion = contenido;

var punto = contenido.IndexOf('.');

if (punto > 0)
{
    primeraOracion =
        contenido[..(punto + 1)].Trim();
}

var respuestaWeb =
    $"Según {mejorResultado.TipoFuente.ToLowerInvariant()}: " +
    $"{primeraOracion}\n\n" +
    $"Fuente: {mejorResultado.Url}";

            return Ok(new ChatResponse(respuestaWeb));
        }

        // 4. No inventar una respuesta si tampoco encontramos
        //    información suficiente en Internet.
        return Ok(new ChatResponse(
            "No encontré información verificada sobre esa consulta."
        ));
    }
}