using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using CorrientesIA.Data;
using CorrientesIA.Training.Model;
using CorrientesIA.Training.Tokenizer;
using TorchSharp;
using static TorchSharp.torch;
using System.Text;
Console.WriteLine("=== CorrientesIA - Entrenamiento ===\n");

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var carpetaCorpus = Path.Combine(AppContext.BaseDirectory, config["TrainingSettings:CarpetaCorpusRespaldo"] ?? "../../../data/corpus");

// Si se configura explicitamente TrainingSettings:CarpetaCheckpoints, se usa esa
// (relativa a la carpeta del binario). Si no, se guarda directo en la carpeta
// model/ compartida del repo (la misma que lee la Api en InferenceService).
var carpetaCheckpointsConfig = config["TrainingSettings:CarpetaCheckpoints"];
var carpetaCheckpoints = carpetaCheckpointsConfig is not null
    ? Path.Combine(AppContext.BaseDirectory, carpetaCheckpointsConfig)
    : RepoPaths.CarpetaModelo();
var vocabSize = config.GetValue<int>("TrainingSettings:TokenizerVocabSize", 8000);
Directory.CreateDirectory(carpetaCheckpoints);

// ---------- 1. Cargar corpus: primero intenta MySQL, si no hay, usa el respaldo en disco ----------
var textos = new List<string>();

try
{
    var connStr = config.GetConnectionString("Default");
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseMySql(connStr, ServerVersion.AutoDetect(connStr))
        .Options;
    using var db = new AppDbContext(options);
    await db.Database.CanConnectAsync();

    var documentos = await db.CorpusDocumentos
    .Select(d => new { d.Id, d.Titulo, d.Fuente, d.Contenido })
    .ToListAsync();

textos = documentos.Select(d => d.Contenido).ToList();

Console.WriteLine("\n--- DOCUMENTOS DEL CORPUS ---");
foreach (var doc in documentos)
{
    Console.WriteLine($"ID: {doc.Id} | Titulo: {doc.Titulo} | Fuente: {doc.Fuente} | Caracteres: {doc.Contenido.Length}");
}
Console.WriteLine("--- FIN DOCUMENTOS ---\n");
    Console.WriteLine($"Corpus cargado desde MySQL: {textos.Count} documentos.");
}
catch
{
    Console.WriteLine("MySQL no disponible, busco el respaldo en disco del scraper...");
}

if (textos.Count == 0 && Directory.Exists(carpetaCorpus))
{
    textos = Directory.GetFiles(carpetaCorpus, "*.txt")
    .Select(path => File.ReadAllText(path, Encoding.UTF8))
    .ToList();
    Console.WriteLine($"Corpus cargado desde disco ({carpetaCorpus}): {textos.Count} archivos.");
}

if (textos.Count == 0)
{
    Console.WriteLine("\nNo hay corpus todavia. Corre primero el proyecto CorrientesIA.Scraper:");
    Console.WriteLine("  dotnet run --project ../CorrientesIA.Scraper");
    return;
}

// ---------- 2. Entrenar (o cargar) el tokenizador BPE ----------
var pathTokenizer = Path.Combine(carpetaCheckpoints, "tokenizer.json");
BpeTokenizer tokenizer;

if (File.Exists(pathTokenizer))
{
    Console.WriteLine($"\nTokenizador ya existe en {pathTokenizer}, lo cargo.");
    tokenizer = BpeTokenizer.Load(pathTokenizer);
}
else
{
    Console.WriteLine($"\nEntrenando tokenizador BPE (vocabSize={vocabSize}) sobre {textos.Count} documentos...");
    tokenizer = new BpeTokenizer();
    tokenizer.Train(textos, vocabSize);
    tokenizer.Save(pathTokenizer);
    Console.WriteLine($"Tokenizador entrenado y guardado en {pathTokenizer}. Vocabulario final: {tokenizer.Vocab.Count} tokens.");
}

// ---------- 3. Prueba rapida: codificar/decodificar una frase de ejemplo ----------
var ejemplo = "Los Esteros del Ibera son una reserva natural de la provincia de Corrientes.";
var ids = tokenizer.Encode(ejemplo);
var reconstruido = tokenizer.Decode(ids);

Console.WriteLine("\n--- Prueba del tokenizador ---");
Console.WriteLine($"Original:      {ejemplo}");
Console.WriteLine($"Tokens (ids):  [{string.Join(", ", ids.Take(20))}{(ids.Length > 20 ? ", ..." : "")}] ({ids.Length} tokens)");
Console.WriteLine($"Reconstruido:  {reconstruido}");

// ---------- 4. Arquitectura del modelo ----------
var gptConfig = new GptMiniConfig { VocabSize = tokenizer.Vocab.Count };
Console.WriteLine($"\nGptMiniConfig listo -> vocab: {gptConfig.VocabSize}, capas: {gptConfig.NumLayers}, " +
                   $"dim: {gptConfig.EmbeddingDim}, heads: {gptConfig.NumHeads}, contexto: {gptConfig.ContextLength}");

// ---------- 5. Tokenizar el corpus completo en una sola secuencia larga ----------
Console.WriteLine("\nTokenizando corpus completo para entrenamiento...");
var eosId = (long)tokenizer.Vocab[BpeTokenizer.EosToken];
var idsCompletos = new List<long>();
foreach (var texto in textos)
{
    idsCompletos.AddRange(tokenizer.Encode(texto).Select(i => (long)i));
    idsCompletos.Add(eosId);
}
Console.WriteLine($"Corpus tokenizado: {idsCompletos.Count} tokens totales.");

if (idsCompletos.Count < gptConfig.ContextLength + 1)
{
    Console.WriteLine("\nCorpus demasiado chico para el contexto configurado " +
                       $"(hacen falta al menos {gptConfig.ContextLength + 1} tokens). " +
                       "Agrega mas fuentes en CorrientesIA.Scraper/fuentes.json y volve a correr el scraper.");
    return;
}

// ---------- 6. Entrenamiento ----------
var pathCheckpoint = Path.Combine(carpetaCheckpoints, "gpt-mini.pt");
var model = new GptMini(gptConfig);

if (File.Exists(pathCheckpoint))
{
    Console.WriteLine($"\nCheckpoint existente en {pathCheckpoint}, cargo pesos y sigo entrenando desde ahi.");
    model.load(pathCheckpoint);
}

Console.WriteLine($"\nEntrenando {gptConfig.Epochs} epocas (batch={gptConfig.BatchSize}, lr={gptConfig.LearningRate})...\n");
Entrenar(model, idsCompletos, gptConfig);

model.save(pathCheckpoint);
Console.WriteLine($"\nModelo guardado en {pathCheckpoint}");
Console.WriteLine("\n--- PRUEBA DE LOSS ---");

model.eval();

var pruebaTexto = "Corrientes es la capital de la provincia";
var pruebaIds = tokenizer.Encode(pruebaTexto)
    .Select(i => (long)i)
    .ToArray();

var pruebaInputs = tensor(pruebaIds).reshape(1, pruebaIds.Length);


var pruebaTargetsData = new long[1, pruebaIds.Length];

for (int i = 0; i < pruebaIds.Length; i++)
{
    pruebaTargetsData[0, i] = pruebaIds[(i + 1) % pruebaIds.Length];
}

var pruebaTargets = tensor(pruebaTargetsData);

using var logitsPrueba = model.forward(pruebaInputs);

var lossPrueba = nn.CrossEntropyLoss().forward(
    logitsPrueba.reshape(-1, gptConfig.VocabSize),
    pruebaTargets.reshape(-1)
);

Console.WriteLine($"Loss de prueba: {lossPrueba.item<float>():F4}");
// ---------- 7. Prueba de generacion con el modelo recien entrenado ----------
Console.WriteLine("\n--- Prueba de generacion ---");
var promptPrueba = "Los Esteros del Ibera";
var promptIds = tokenizer.Encode(promptPrueba).Select(i => (long)i).ToArray();
var generado = model.Generate(promptIds, maxNewTokens: 40, contextLength: gptConfig.ContextLength, temperature: 0.8, eosId: eosId);
var textoGenerado = tokenizer.Decode(generado.Select(i => (int)i).ToArray());
Console.WriteLine($"Prompt:    {promptPrueba}");
Console.WriteLine($"Generado:  {textoGenerado}");

// ---------- funciones locales ----------

static void Entrenar(GptMini model, List<long> corpusIds, GptMiniConfig cfg)
{
    var optimizer = optim.Adam(model.parameters(), lr: cfg.LearningRate);
    var lossFn = nn.CrossEntropyLoss();
    var rng = new Random(42);

    // limitamos pasos por epoca para que un entrenamiento local en CPU
    // termine en tiempos razonables; se puede subir una vez que ande bien.
    var pasosPorEpoca = Math.Min(200, Math.Max(1, (corpusIds.Count - cfg.ContextLength - 1) / cfg.BatchSize));

    model.train();

    for (int epoca = 1; epoca <= cfg.Epochs; epoca++)
    {
        double perdidaAcumulada = 0;

        for (int paso = 0; paso < pasosPorEpoca; paso++)
        {
            var (inputs, targets) = MuestrearBatch(corpusIds, cfg, rng);

            optimizer.zero_grad();
            var logits = model.forward(inputs); // (batch, ctx, vocab)
            var loss = lossFn.forward(logits.reshape(-1, cfg.VocabSize), targets.reshape(-1));

            loss.backward();
            optimizer.step();

            perdidaAcumulada += loss.item<float>();
        }

        Console.WriteLine($"  Epoca {epoca}/{cfg.Epochs} - loss promedio: {perdidaAcumulada / pasosPorEpoca:F4}");
    }
}

static (Tensor inputs, Tensor targets) MuestrearBatch(List<long> corpusIds, GptMiniConfig cfg, Random rng)
{
    var inputsData = new long[cfg.BatchSize, cfg.ContextLength];
    var targetsData = new long[cfg.BatchSize, cfg.ContextLength];

    for (int b = 0; b < cfg.BatchSize; b++)
    {
        var inicio = rng.Next(0, corpusIds.Count - cfg.ContextLength - 1);
        for (int t = 0; t < cfg.ContextLength; t++)
        {
            inputsData[b, t] = corpusIds[inicio + t];
            targetsData[b, t] = corpusIds[inicio + t + 1];
        }
    }

    return (tensor(inputsData), tensor(targetsData));
}
