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

        // 2. Si no existe una coincidencia directa, buscar en el corpus
        //    propio (respuestas estructuradas: población, ubicación,
        //    definición, fecha, listas de localidades, meta-consultas).
        var datoCorpus =
            await _grounding.BuscarEnCorpusAsync(
                request.Mensaje);

        if (datoCorpus is not null)
        {
            return Ok(new ChatResponse(datoCorpus));
        }

        // 3. Consulta "general": no hay una respuesta estructurada, pero
        //    sí un documento del corpus relacionado. En vez de devolver
        //    ese documento armado a mano ("Titulo: oracion"), se lo
        //    pasamos como contexto al modelo GptMini entrenado y dejamos
        //    que él redacte la respuesta con sus propias palabras.
        var contexto =
            await _grounding.BuscarContextoGeneralAsync(
                request.Mensaje);

        if (contexto is not null)
        {
            var prompt =
                $"Contexto sobre {contexto.Value.Titulo}: {contexto.Value.Contenido}\n" +
                $"Pregunta: {request.Mensaje}";

            var respuestaGenerada =
                await _inference.GenerarRespuestaAsync(prompt);

            if (!string.IsNullOrWhiteSpace(respuestaGenerada) &&
                !respuestaGenerada.StartsWith("(modelo aun no entrenado)", StringComparison.Ordinal) &&
                !respuestaGenerada.StartsWith("(el modelo no genero texto util", StringComparison.Ordinal))
            {
                return Ok(new ChatResponse(respuestaGenerada));
            }
        }

        // 4. Ultimo recurso: búsqueda web.
        var resultadosWeb =
            await _webSearch.BuscarAsync(
                request.Mensaje,
                cancellationToken);

        var mejorResultado =
            resultadosWeb.FirstOrDefault();
    foreach (var resultado in resultadosWeb)
{
    Console.WriteLine(
        $"WEB: [{resultado.TipoFuente}] " +
        $"REL={resultado.RelevanciaConsulta} " +
        $"PRIO={resultado.PrioridadFuente} " +
        $"{resultado.Title}");
}
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

var respuestaWeb =
    $"Según {mejorResultado.TipoFuente.ToLowerInvariant()}: " +
    $"{contenido}\n\n" +
    $"Fuente: {mejorResultado.Url}";

            return Ok(new ChatResponse(respuestaWeb));
        }

        // 5. No inventar una respuesta si tampoco encontramos
        //    información suficiente en Internet.
        return Ok(new ChatResponse(
            "No encontré información verificada sobre esa consulta."
        ));
    }
}