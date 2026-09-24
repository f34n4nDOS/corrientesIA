using CorrientesIA.Data;
using CorrientesIA.Training.Model;
using CorrientesIA.Training.Tokenizer;
using Microsoft.EntityFrameworkCore;
using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine("=== CorrientesIA - GPT-mini ===");
Console.WriteLine();

var connectionString =
    Environment.GetEnvironmentVariable("ConnectionStrings__Default")
    ?? "Server=localhost;Port=3306;Database=corrientesia;User=root;Password=changeme;CharSet=utf8mb4;";


var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseMySql(
        connectionString,
        ServerVersion.AutoDetect(connectionString))
    .Options;

await using var db = new AppDbContext(options);

var documentos = await db.CorpusDocumentos
    .Where(x => !string.IsNullOrWhiteSpace(x.Contenido))
    .OrderBy(x => x.Id)
    .ToListAsync();

Console.WriteLine(
    $"Documentos encontrados: {documentos.Count}");

if (documentos.Count == 0)
{
    Console.WriteLine(
        "No hay documentos para entrenar.");
    return;
}

foreach (var documento in documentos)
{
    Console.WriteLine(
        $"  [{documento.Id}] {documento.Titulo} - " +
        $"{documento.Contenido.Length} caracteres");
}

var config = new GptMiniConfig
{
    Epochs = 20,
    LearningRate = 3e-4,
    BatchSize = 1
};

var outputDirectory = Path.Combine(
    AppContext.BaseDirectory,
    "checkpoints");

Directory.CreateDirectory(
    outputDirectory);

var tokenizerPath = Path.Combine(
    outputDirectory,
    "tokenizer.json");

var modelPath = Path.Combine(
    outputDirectory,
    "gpt-mini.pt");

var generateMode = args.Contains(
    "--generate");


// ============================================================
// MODO GENERACIÓN
// ============================================================

if (generateMode)
{
    Console.WriteLine();
    Console.WriteLine(
        "=== GENERACIÓN GPT-mini ===");

    if (!File.Exists(tokenizerPath))
    {
        Console.WriteLine(
            $"No existe el tokenizer: {tokenizerPath}");
        return;
    }

    if (!File.Exists(modelPath))
    {
        Console.WriteLine(
            $"No existe el modelo: {modelPath}");
        return;
    }

    Console.WriteLine(
        "Cargando tokenizer...");

    var tokenizer = BpeTokenizer.Load(
        tokenizerPath);

    Console.WriteLine(
        $"Tokenizer cargado: " +
        $"{tokenizer.Vocab.Count} tokens");

    Console.WriteLine(
        "Cargando modelo...");

    using var model =
        new GptMini(config);

    model.load(modelPath);
    model.eval();

    Console.WriteLine(
        "Modelo cargado correctamente.");

    Console.WriteLine();
    Console.WriteLine(
        "=== GENERACIÓN ===");

    var prompt = "Corrientes es";

    var promptTokens = tokenizer
        .Encode(prompt)
        .ToArray();

    if (promptTokens.Length == 0)
    {
        Console.WriteLine(
            "El prompt no produjo tokens.");
        return;
    }

    var generatedIds = promptTokens
        .Select(x => (long)x)
        .ToList();

    Console.WriteLine(
        $"Prompt: {prompt}");

    Console.WriteLine(
        $"Tokens iniciales: " +
        $"{generatedIds.Count}");

    const int maxNewTokens = 30;

    // Temperatura:
    // 1.0 = distribución original
    // < 1.0 = más conservador
    // > 1.0 = más variado
    const double temperature = 0.8;

    for (
        var step = 0;
        step < maxNewTokens;
        step++)
    {
        var currentLength =
            generatedIds.Count;

        if (currentLength >=
            config.ContextLength)
        {
            break;
        }

        using var currentInput =
            torch.tensor(
                generatedIds.ToArray(),
                dtype: ScalarType.Int64)
            .reshape(
                1,
                currentLength);

        using var logits =
            model.forward(
                currentInput);

        using var lastLogits =
            logits[0, -1];

        using var scaledLogits =
            lastLogits / temperature;

        using var probabilities =
            functional.softmax(
                scaledLogits,
                dim: 0);

        using var sampled =
            torch.multinomial(
                probabilities,
                1);

        var nextToken =
            sampled.item<long>();

        generatedIds.Add(
            nextToken);
    }

    var result =
        tokenizer.Decode(
            generatedIds
                .Select(x => (int)x)
                .ToArray());

    Console.WriteLine();
    Console.WriteLine(
        "=== RESULTADO ===");

    Console.WriteLine(
        result);

    Console.WriteLine();
    Console.WriteLine(
        $"Tokens generados: " +
        $"{generatedIds.Count - promptTokens.Length}");

    return;
}


// ============================================================
// ENTRENAMIENTO
// ============================================================

Console.WriteLine();
Console.WriteLine(
    "=== ENTRENAMIENTO GPT-mini ===");

var corpus = documentos
    .Select(x =>
        $"{x.Titulo}\n{x.Contenido}")
    .ToList();


// ============================================================
// TOKENIZER
// ============================================================

Console.WriteLine();
Console.WriteLine(
    "=== TOKENIZER ===");

var tokenizerTrain =
    new BpeTokenizer();

tokenizerTrain.Train(
    corpus,
    config.VocabSize);

Console.WriteLine(
    $"Vocabulario: " +
    $"{tokenizerTrain.Vocab.Count}");

tokenizerTrain.Save(
    tokenizerPath);

Console.WriteLine(
    $"Tokenizer guardado: " +
    $"{tokenizerPath}");

var textoCorpus =
    string.Join(
        "\n\n",
        corpus);

var encoded =
    tokenizerTrain.Encode(
        textoCorpus);

Console.WriteLine(
    $"Tokens totales: " +
    $"{encoded.Length}");

// Contexto efectivo: si el corpus es más chico que ContextLength,
// lo acotamos para no pedir ventanas imposibles de armar.
var effectiveContextLength =
    Math.Min(
        config.ContextLength,
        encoded.Length - 2);

if (effectiveContextLength < 8)
{
    Console.WriteLine(
        "Corpus demasiado chico para entrenar " +
        "con este ContextLength. Sumá más documentos " +
        "o bajá ContextLength.");
    return;
}

Console.WriteLine(
    $"Contexto efectivo: {effectiveContextLength}");

var tokensLong = encoded
    .Select(x => (long)x)
    .ToArray();


// ============================================================
// MUESTREO DE BATCHES ALEATORIOS
// ============================================================

// En vez de entrenar siempre sobre la misma ventana fija,
// en cada step tomamos `BatchSize` ventanas de `effectiveContextLength`
// tokens arrancando en posiciones aleatorias del corpus completo.
// Así el modelo termina viendo todo el corpus, no solo el principio.
(Tensor input, Tensor target) MuestrearBatch(Random rng)
{
    var inputData = new long[config.BatchSize, effectiveContextLength];
    var targetData = new long[config.BatchSize, effectiveContextLength];

    for (var b = 0; b < config.BatchSize; b++)
    {
        var start = rng.Next(
            0,
            tokensLong.Length - effectiveContextLength - 1);

        for (var t = 0; t < effectiveContextLength; t++)
        {
            inputData[b, t] = tokensLong[start + t];
            targetData[b, t] = tokensLong[start + t + 1];
        }
    }

    var inputTensor = tensor(inputData, dtype: ScalarType.Int64);
    var targetTensor = tensor(targetData, dtype: ScalarType.Int64);

    return (inputTensor, targetTensor);
}


// ============================================================
// MODELO
// ============================================================

Console.WriteLine();
Console.WriteLine(
    "=== MODELO ===");

using var modelTrain =
    new GptMini(config);

Console.WriteLine(
    $"Capas: {config.NumLayers}");

Console.WriteLine(
    $"Embedding: " +
    $"{config.EmbeddingDim}");

Console.WriteLine(
    $"Heads: " +
    $"{config.NumHeads}");

Console.WriteLine(
    $"Vocabulario: " +
    $"{config.VocabSize}");


// ============================================================
// OPTIMIZADOR
// ============================================================

Console.WriteLine();
Console.WriteLine(
    "=== OPTIMIZADOR ===");

var parameters =
    modelTrain.parameters();

Console.WriteLine(
    $"Parámetros: " +
    $"{parameters.Count()}");

using var optimizer =
    optim.Adam(
        parameters,
        lr: config.LearningRate);

Console.WriteLine(
    $"Learning rate: " +
    $"{config.LearningRate}");


// ============================================================
// ENTRENAMIENTO
// ============================================================

Console.WriteLine();
Console.WriteLine(
    "=== ENTRENAMIENTO ===");

var rng = new Random(42);
var totalSteps = config.Epochs * config.StepsPerEpoch;

Console.WriteLine(
    $"Steps totales: {totalSteps} " +
    $"({config.Epochs} epochs x {config.StepsPerEpoch} steps)");

modelTrain.train();

for (var step = 1; step <= totalSteps; step++)
{
    var (input, target) = MuestrearBatch(rng);

    optimizer.zero_grad();

    using var logits =
        modelTrain.forward(input);

    using var logitsFlat =
        logits.reshape(
            config.BatchSize * effectiveContextLength,
            config.VocabSize);

    using var targetFlat =
        target.reshape(
            config.BatchSize * effectiveContextLength);

    using var loss =
        functional.cross_entropy(
            logitsFlat,
            targetFlat);

    loss.backward();
    optimizer.step();

    input.Dispose();
    target.Dispose();

    if (step % 10 == 0 || step == totalSteps)
    {
        Console.WriteLine(
            $"Step {step,4}/{totalSteps} - " +
            $"loss: {loss.item<float>():F6}");
    }

    if (step % 200 == 0)
    {
        modelTrain.save(modelPath);
        Console.WriteLine(
            $"  Checkpoint intermedio guardado " +
            $"({step} steps).");
    }
}


// ============================================================
// CHECKPOINT FINAL
// ============================================================

Console.WriteLine();
Console.WriteLine(
    "=== CHECKPOINT ===");

modelTrain.save(
    modelPath);

Console.WriteLine(
    $"Modelo guardado: {modelPath}");


// ============================================================
// PRUEBA DE CARGA
// ============================================================

Console.WriteLine();
Console.WriteLine(
    "=== PRUEBA DE CARGA ===");

using var loadedModel =
    new GptMini(config);

loadedModel.load(
    modelPath);

loadedModel.eval();

var (testInput, _) = MuestrearBatch(rng);

using var loadedLogits =
    loadedModel.forward(testInput);

testInput.Dispose();

Console.WriteLine(
    "Modelo cargado correctamente.");

Console.WriteLine(
    $"Logits cargados: " +
    $"[{loadedLogits.shape[0]}, " +
    $"{loadedLogits.shape[1]}, " +
    $"{loadedLogits.shape[2]}]");

Console.WriteLine();
Console.WriteLine(
    "======================================");

Console.WriteLine(
    "CHECKPOINT OK");

Console.WriteLine(
    "======================================");