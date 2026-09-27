
using CorrientesIA.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace CorrientesIA.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly InferenceService _inference;
    private readonly GroundingService _grounding;

    public ChatController(
        InferenceService inference,
        GroundingService grounding)
    {
        _inference = inference;
        _grounding = grounding;
    }

    public record ChatRequest(string Mensaje);
    public record ChatResponse(string Respuesta);

    [HttpPost]
    public async Task<ActionResult<ChatResponse>> Post(
        [FromBody] ChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Mensaje))
        {
            return BadRequest("El mensaje no puede estar vacio.");
        }

        // 1. Buscar primero en datos verificados directos.
        var datoVerificado =
            await _grounding.BuscarPorPalabrasClaveAsync(
                request.Mensaje);

        if (datoVerificado is not null)
        {
            return Ok(new ChatResponse(datoVerificado));
        }

        // 2. Si no hay dato directo, buscar en el corpus.
        var datoCorpus =
            await _grounding.BuscarEnCorpusAsync(
                request.Mensaje);

        if (datoCorpus is not null)
        {
            return Ok(new ChatResponse(datoCorpus));
        }

        // 3. Si no existe información verificada,
        // informar que no se encontró conocimiento.
        return Ok(new ChatResponse(
            "No encontré información verificada sobre esa consulta en mi base de conocimientos."
        ));
    }
}

