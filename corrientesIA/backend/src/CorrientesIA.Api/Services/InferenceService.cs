namespace CorrientesIA.Api.Services;

public class InferenceService
{
    private readonly ILogger<InferenceService> _logger;
    private readonly GroundingService _grounding;
    private bool _modelLoaded = false;

    public InferenceService(
        ILogger<InferenceService> logger,
        GroundingService grounding)
    {
        _logger = logger;
        _grounding = grounding;
    }

    public async Task<string> GenerarRespuestaAsync(string prompt)
    {
        if (!_modelLoaded)
        {
            var texto = prompt.ToLowerInvariant();

            if (texto.Contains("población") ||
                texto.Contains("poblacion") ||
                texto.Contains("habitantes"))
            {
                var dato = await _grounding.BuscarDatoDuroAsync("poblacion_capital_2022");

                if (dato != null)
                    return $"La población de Corrientes Capital según el Censo 2022 es de {dato} habitantes. Fuente: INDEC Censo 2022.";
            }

            if (texto.Contains("fundación") ||
                texto.Contains("fundacion"))
            {
                var dato = await _grounding.BuscarDatoDuroAsync("fundacion_ciudad_corrientes");

                if (dato != null)
                    return $"La ciudad de Corrientes fue fundada el {dato}. Fuente: Wikipedia.";
            }

            return "(modelo aun no entrenado) Eco de tu consulta: " + prompt;
        }

        return string.Empty;
    }
}
