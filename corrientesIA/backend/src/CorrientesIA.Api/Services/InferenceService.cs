using CorrientesIA.Training.Model;
using CorrientesIA.Training.Tokenizer;
using Microsoft.Extensions.DependencyInjection;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace CorrientesIA.Api.Services;

public class InferenceService
{
    private readonly ILogger<InferenceService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private readonly bool _modelLoaded;
    private readonly BpeTokenizer? _tokenizer;
    private readonly GptMini? _model;
    private readonly GptMiniConfig? _config;

    public InferenceService(
        ILogger<InferenceService> logger,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;

        var tokenizerPath = configuration["ModelSettings:TokenizerPath"] ?? "checkpoints/tokenizer.json";
        var checkpointPath = configuration["ModelSettings:CheckpointPath"] ?? "checkpoints/gpt-mini.pt";

        if (File.Exists(tokenizerPath) && File.Exists(checkpointPath))
        {
            try
            {
                _tokenizer = BpeTokenizer.Load(tokenizerPath);

                // VocabSize tiene que ser el mismo valor con el que se entrenó (el default de
// GptMiniConfig, 8000), NO la cantidad real de palabras del tokenizer.
_config = new GptMiniConfig
{
    ContextLength = 128,
    EmbeddingDim = 192,
    NumLayers = 4,
    NumHeads = 4,
    Dropout = 0.1
};clear


                _model = new GptMini(_config);
                _model.load(checkpointPath);
                _model.eval();

                _modelLoaded = true;
                _logger.LogInformation(
                    "Modelo GPT-mini cargado correctamente ({Vocab} tokens de vocabulario).",
                    _tokenizer.Vocab.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cargando el modelo entrenado. Se usará el modo de eco.");
                _modelLoaded = false;
            }
        }
        else
        {
            _logger.LogWarning(
                "No se encontró el modelo entrenado en {TokenizerPath} / {CheckpointPath}. Corriendo en modo eco.",
                tokenizerPath, checkpointPath);
        }
    }

    public async Task<string> GenerarRespuestaAsync(string prompt)
    {
        using var scope = _scopeFactory.CreateScope();
        var grounding = scope.ServiceProvider.GetRequiredService<GroundingService>();

        var texto = prompt.ToLowerInvariant();

        if (texto.Contains("población") || texto.Contains("poblacion") || texto.Contains("habitantes"))
        {
            var dato = await grounding.BuscarDatoDuroAsync("poblacion_capital_2022");
            if (dato != null)
                return $"La población de Corrientes Capital según el Censo 2022 es de {dato} habitantes. Fuente: INDEC Censo 2022.";
        }

        if (texto.Contains("fundación") || texto.Contains("fundacion"))
        {
            var dato = await grounding.BuscarDatoDuroAsync("fundacion_ciudad_corrientes");
            if (dato != null)
                return $"La ciudad de Corrientes fue fundada el {dato}. Fuente: Wikipedia.";
        }

        if (!_modelLoaded || _tokenizer is null || _model is null || _config is null)
            return "(modelo aun no entrenado) Eco de tu consulta: " + prompt;

        return GenerarConModelo(prompt);
    }

    private string GenerarConModelo(string prompt)
    {
        var promptTokens = _tokenizer!.Encode(prompt);
        if (promptTokens.Length == 0)
            return "No pude interpretar la consulta.";

        var generatedIds = promptTokens.Select(x => (long)x).ToList();
        const int maxNewTokens = 40;
        const double temperature = 0.8;

        using (no_grad())
        {
            for (var step = 0; step < maxNewTokens; step++)
            {
                var currentLength = generatedIds.Count;
                if (currentLength >= _config!.ContextLength) break;

                using var currentInput = tensor(generatedIds.ToArray(), dtype: ScalarType.Int64)
                    .reshape(1, currentLength);

                using var logits = _model!.forward(currentInput);
                using var lastLogits = logits[0, -1];
                using var scaledLogits = lastLogits / temperature;
                using var probabilities = functional.softmax(scaledLogits, dim: 0);
                using var sampled = multinomial(probabilities, 1);

                generatedIds.Add(sampled.item<long>());
            }
        }

        return _tokenizer.Decode(generatedIds.Select(x => (int)x).ToArray());
    }
}