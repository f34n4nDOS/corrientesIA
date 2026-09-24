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
    "Server=mysql;Port=3306;Database=corrientesia;User=root;Password=changeme;CharSet=utf8mb4;";


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

if (encoded.Length < 3)
{
    Console.WriteLine(
        "No hay suficientes tokens " +
        "para entrenar.");
    return;
}

var sequenceLength =
    Math.Min(
        config.ContextLength,
        encoded.Length - 1);

Console.WriteLine(
    $"Longitud de secuencia: " +
    $"{sequenceLength}");

var inputTokens = encoded
    .Take(sequenceLength)
    .Select(x => (long)x)
    .ToArray();

var targetTokens = encoded
    .Skip(1)
    .Take(sequenceLength)
    .Select(x => (long)x)
    .ToArray();

using var input =
    tensor(
        inputTokens,
        dtype: ScalarType.Int64)
    .reshape(
        1,
        sequenceLength);

using var targets =
    tensor(
        targetTokens,
        dtype: ScalarType.Int64)
    .reshape(
        1,
        sequenceLength);


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

modelTrain.train();

for (
    var epoch = 1;
    epoch <= config.Epochs;
    epoch++)
{
    optimizer.zero_grad();

    using var logits =
        modelTrain.forward(
            input);

    using var logitsFlat =
        logits.reshape(
            sequenceLength,
            config.VocabSize);

    using var targetsFlat =
        targets.reshape(
            sequenceLength);

    using var loss =
        functional.cross_entropy(
            logitsFlat,
            targetsFlat);

    loss.backward();

    optimizer.step();

    var lossValue =
        loss.item<float>();

    Console.WriteLine(
        $"Epoch {epoch,2}/" +
        $"{config.Epochs} - " +
        $"loss: {lossValue:F6}");
}


// ============================================================
// CHECKPOINT
// ============================================================

Console.WriteLine();
Console.WriteLine(
    "=== CHECKPOINT ===");

modelTrain.save(
    modelPath);

Console.WriteLine(
    $"Modelo guardado: " +
    $"{modelPath}");


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

using var loadedLogits =
    loadedModel.forward(
        input);

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
