using CorrientesIA.Data;
using CorrientesIA.Training.Model;
using CorrientesIA.Training.Tokenizer;
using TorchSharp;
using static TorchSharp.torch;

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

    // Tamaño del n-grama que no se permite repetir durante la generación.
    // 3 = no se repite ninguna secuencia de 3 tokens consecutivos ya
    // generada antes. Es el mismo mecanismo que usa HuggingFace
    // (no_repeat_ngram_size) para frenar los loops típicos de modelos
    // chicos con poco corpus: "la ciudad de corrientes. la ciudad de
    // corrientes." nunca pasaría de la segunda repetición porque el
    // tercer token que completaría el n-grama repetido queda bloqueado.
    private const int NoRepeatNgramSize = 3;

    // Si al bloquear n-gramas repetidos la probabilidad máxima entre los
    // tokens que quedan permitidos cae por debajo de esto, es señal de que
    // estamos forzando al modelo a "adivinar" en vez de dejarlo decir algo
    // que realmente aprendió. Cortamos ahí — una respuesta corta y
    // coherente es mejor que una larga que degenera en ruido
    // ("carg2022", "corsobre el caballo porriente").
    private const float ConfianzaMinima = 0.15f;

    public InferenceService(ILogger<InferenceService> logger, IConfiguration config)
    {
        _logger = logger;

        // En Docker/produccion, ModelSettings:CheckpointDir viene seteado explicitamente
        // (variable de entorno ModelSettings__CheckpointDir -> /app/model). En desarrollo
        // local, si no esta seteado, se autodetecta la carpeta model/ del repo.
        var carpetaCheckpoints = config["ModelSettings:CheckpointDir"] ?? RepoPaths.CarpetaModelo();
        var pathTokenizer = Path.Combine(carpetaCheckpoints, "tokenizer.json");
        var pathModelo = Path.Combine(carpetaCheckpoints, "gpt-mini-qa.pt");

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
                _logger.LogError(ex, "Error al cargar el modelo GptMini desde {Path}", pathModelo);
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

        var promptFormateado = $"{prompt}\nRespuesta:";
        var promptIds = _tokenizer.Encode(promptFormateado).Select(i => (long)i).ToArray();
        var eosId = (long)_tokenizer.Vocab[BpeTokenizer.EosToken];

        var generado = GenerarConBloqueoDeRepeticion(promptIds, eosId);
        var soloGenerado = generado.Skip(promptIds.Length).Select(i => (int)i).ToArray();

        var texto = _tokenizer.Decode(soloGenerado);
        if (string.IsNullOrWhiteSpace(texto))
            return Task.FromResult("(el modelo no genero texto util para esa consulta)");

        return Task.FromResult(LimpiarRespuesta(texto));
    }

    /// <summary>
    /// Generación autoregresiva con sampling por temperatura, igual que
    /// GptMini.Generate, pero bloqueando cualquier token que complete un
    /// n-grama ya visto en la secuencia generada. Vive acá (no en
    /// GptMini.cs) para no tocar el proyecto de Training a ciegas; si
    /// preferís que esta lógica viva en el modelo en vez de en la API,
    /// pasame GptMini.cs y la movemos ahí.
    /// </summary>
    private long[] GenerarConBloqueoDeRepeticion(long[] promptIds, long eosId)
    {
        var generados = new List<long>(promptIds);

        // n-gramas ya emitidos, como clave "tok1,tok2" (los primeros
        // NoRepeatNgramSize - 1 tokens) -> conjunto de tokens que YA
        // completaron ese prefijo alguna vez, y por lo tanto quedan
        // vetados la próxima vez que aparezca el mismo prefijo.
        var ngramasVistos = new Dictionary<string, HashSet<long>>();

        _model!.eval();

        using (no_grad())
        {
            for (int paso = 0; paso < MaxNewTokens; paso++)
            {
                var contexto = generados
                    .Skip(Math.Max(0, generados.Count - ContextLength))
                    .ToArray();

                using var input = tensor(contexto).unsqueeze(0);
                using var logits = _model.forward(input);

                var seqLen = (int)logits.shape[1];
                using var ultimoLogit = logits.select(1, seqLen - 1).squeeze(0);
                using var escalado = ultimoLogit / Temperature;
                using var probs = nn.functional.softmax(escalado, dim: 0);

                var probsArray = probs.data<float>().ToArray();

                // Si tenemos suficiente historial, bloqueamos los tokens
                // que repetirían un n-grama exacto ya generado.
                if (generados.Count >= NoRepeatNgramSize - 1)
                {
                    var prefijo = generados
                        .Skip(generados.Count - (NoRepeatNgramSize - 1))
                        .ToArray();
                    var clavePrefijo = string.Join(",", prefijo);

                    if (ngramasVistos.TryGetValue(clavePrefijo, out var tokensVetados))
                    {
                        foreach (var tokenVetado in tokensVetados)
                        {
                            if (tokenVetado >= 0 && tokenVetado < probsArray.Length)
                                probsArray[tokenVetado] = 0f;
                        }
                    }
                }

                var suma = probsArray.Sum();
                if (suma <= 0f)
                {
                    // Bloqueamos todo (caso raro, corpus muy chico): dejamos
                    // de forzar y volvemos a la distribución original para
                    // no trabarnos sin poder generar nada más.
                    probsArray = probs.data<float>().ToArray();
                    suma = probsArray.Sum();
                }

                for (int i = 0; i < probsArray.Length; i++)
                    probsArray[i] /= suma;

                // Si ya generamos algo y el mejor token permitido tiene
                // confianza baja, el bloqueo de n-gramas nos está forzando
                // a elegir entre opciones que el modelo no aprendió bien.
                // Mejor cortar acá que seguir y degenerar en ruido.
                if (generados.Count > promptIds.Length && probsArray.Max() < ConfianzaMinima)
                    break;

                using var probsAjustado = tensor(probsArray);
                var siguiente = multinomial(probsAjustado, 1).item<long>();

                // Registrar el n-grama que se acaba de completar con este
                // token, para vetarlo si el mismo prefijo vuelve a salir.
                if (generados.Count >= NoRepeatNgramSize - 1)
                {
                    var prefijo = generados
                        .Skip(generados.Count - (NoRepeatNgramSize - 1))
                        .ToArray();
                    var clavePrefijo = string.Join(",", prefijo);

                    if (!ngramasVistos.TryGetValue(clavePrefijo, out var set))
                    {
                        set = [];
                        ngramasVistos[clavePrefijo] = set;
                    }
                    set.Add(siguiente);
                }

                generados.Add(siguiente);

                if (siguiente == eosId)
                    break;
            }
        }

        return generados.ToArray();
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