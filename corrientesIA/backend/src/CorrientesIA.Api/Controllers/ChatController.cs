using CorrientesIA.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace CorrientesIA.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly InferenceService _inference;
    private readonly GroundingService _grounding;

    public ChatController(InferenceService inference, GroundingService grounding)
    {
        _inference = inference;
        _grounding = grounding;
    }

    public record ChatRequest(string Mensaje);
    public record ChatResponse(string Respuesta);

    [HttpPost]
    public async Task<ActionResult<ChatResponse>> Post([FromBody] ChatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Mensaje))
            return BadRequest("El mensaje no puede estar vacio.");

        var respuesta = await _inference.GenerarRespuestaAsync(request.Mensaje);
        return Ok(new ChatResponse(respuesta));
    }
}
