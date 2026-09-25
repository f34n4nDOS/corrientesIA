using CorrientesIA.Data;
using CorrientesIA.Training.Model;
using CorrientesIA.Training.Tokenizer;

namespace CorrientesIA.Api.Services;

// Carga el modelo GptMini entrenado (TorchSharp) + el tokenizador BPE propio,
// y genera texto de forma autoregresiva. Si todavia no hay checkpoint
// entrenado, cae a un modo "eco" para que el resto del pipeline (grounding,
// frontend) se pueda probar igual.
public class InferenceService
{
    private readonly ILogger<InferenceService> _logger;
    private readonly GptMini? _model;
    private readonly BpeTokenizer? _tokenizer;
    private readonly bool _modeloDisponible;

    private const int ContextLength = 128;
    private const int MaxNewTokens = 60;
    private const double Temperature = 0.8;

    public InferenceService(ILogger<InferenceService> logger, IConfiguration config)
    {
        _logger = logger;

        // En Docker/produccion, ModelSettings:CheckpointDir viene seteado explicitamente
        // (variable de entorno ModelSettings__CheckpointDir -> /app/model). En desarrollo
        // local, si no esta seteado, se autodetecta la carpeta model/ del repo.
        var carpetaCheckpoints = config["ModelSettings:CheckpointDir"] ?? RepoPaths.CarpetaModelo();
        var pathTokenizer = Path.Combine(carpetaCheckpoints, "tokenizer.json");
        var pathModelo = Path.Combine(carpetaCheckpoints, "gpt-mini.pt");

        Console.WriteLine("========================================");
Console.WriteLine($"CHECKPOINT DIR: {carpetaCheckpoints}");
Console.WriteLine($"TOKENIZER: {pathTokenizer}");
Console.WriteLine($"MODELO: {pathModelo}");
Console.WriteLine($"TOKENIZER EXISTE: {File.Exists(pathTokenizer)}");
Console.WriteLine($"MODELO EXISTE: {File.Exists(pathModelo)}");
Console.WriteLine("========================================");
        if (File.Exists(pathTokenizer) && File.Exists(pathModelo))
        {
            try
            {
                _tokenizer = BpeTokenizer.Load(pathTokenizer);
                var gptConfig = new GptMiniConfig { VocabSize = _tokenizer.Vocab.Count };
                _model = new GptMini(gptConfig);
                _model.load(pathModelo);
                _model.eval();
                _modeloDisponible = true;
                _logger.LogInformation("Modelo GptMini cargado desde {Path} (vocab={Vocab})", pathModelo, _tokenizer.Vocab.Count);
            }
            catch (Exception ex)
{
    Console.WriteLine("========================================");
    Console.WriteLine("ERROR CARGANDO EL MODELO");
    Console.WriteLine("========================================");
    Console.WriteLine(ex.ToString());
    Console.WriteLine("========================================");

    throw;
}
        }
        else
        {
            _logger.LogInformation(
                "No hay checkpoint entrenado en {Carpeta}. Corre CorrientesIA.Training primero. Modo eco activo.",
                carpetaCheckpoints);
        }
    }

    public Task<string> GenerarRespuestaAsync(string prompt)
    {
        if (!_modeloDisponible || _model is null || _tokenizer is null)
        {
            return Task.FromResult("(modelo aun no entrenado) Eco de tu consulta: " + prompt);
        }

        var promptIds = _tokenizer.Encode(prompt).Select(i => (long)i).ToArray();
        var eosId = (long)_tokenizer.Vocab[BpeTokenizer.EosToken];

        var generado = _model.Generate(promptIds, MaxNewTokens, ContextLength, Temperature, eosId);
        var soloGenerado = generado.Skip(promptIds.Length).Select(i => (int)i).ToArray();

                var texto = _tokenizer.Decode(soloGenerado);
        if (string.IsNullOrWhiteSpace(texto))
            return Task.FromResult("(el modelo no genero texto util para esa consulta)");

        return Task.FromResult(LimpiarRespuesta(texto));
    }

    private static string LimpiarRespuesta(string texto)
    {
        // Saca signos de puntuación sueltos al principio (". ", ", ", etc.)
        texto = System.Text.RegularExpressions.Regex.Replace(texto, @"^[\s.,;:!?]+", "");

        // Saca espacios antes de puntuación.
        texto = System.Text.RegularExpressions.Regex.Replace(texto, @"\s+([,.;:!?])", "$1");

        // Colapsa espacios múltiples.
        texto = System.Text.RegularExpressions.Regex.Replace(texto, @"\s{2,}", " ").Trim();

        if (texto.Length == 0)
            return texto;

        // Capitaliza la primera letra.
        texto = char.ToUpperInvariant(texto[0]) + texto[1..];

        // Corta en el último punto/exclamación/interrogación, para no terminar
        // a mitad de palabra si el límite de tokens interrumpió la oración.
        var ultimoCierre = texto.LastIndexOfAny(new[] { '.', '!', '?' });
        if (ultimoCierre > 10)
            texto = texto[..(ultimoCierre + 1)];

        return texto;
    }
    
}
