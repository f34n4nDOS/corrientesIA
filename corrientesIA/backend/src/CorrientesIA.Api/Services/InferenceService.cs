namespace CorrientesIA.Api.Services;

// Carga el modelo entrenado (TorchSharp) y genera texto.
// Se combina con GroundingService para no depender solo de lo que
// el transformer "memorizo" durante el entrenamiento.
public class InferenceService
{
    private readonly ILogger<InferenceService> _logger;
    private bool _modelLoaded = false;

    public InferenceService(ILogger<InferenceService> logger)
    {
        _logger = logger;
        // TODO: cargar GptMini + BpeTokenizer desde el checkpoint indicado en appsettings
    }

    public Task<string> GenerarRespuestaAsync(string prompt)
    {
        if (!_modelLoaded)
        {
            return Task.FromResult(
                "(modelo aun no entrenado) Eco de tu consulta: " + prompt);
        }

        // TODO: tokenizar prompt, correr forward pass, sample con temperatura,
        // decodificar tokens generados.
        return Task.FromResult(string.Empty);
    }
}
