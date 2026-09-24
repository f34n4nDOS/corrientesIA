using CorrientesIA.Training.Model;
using CorrientesIA.Training.Tokenizer;

Console.WriteLine("=== CorrientesIA - Entrenamiento del GPT-mini ===");
Console.WriteLine();
Console.WriteLine("Pasos (todavia por implementar en orden):");
Console.WriteLine("  1. Cargar corpus desde MySQL (CorpusDocumento) o desde /data/corpus/*.txt");
Console.WriteLine("  2. Entrenar/tokenizar con BpeTokenizer");
Console.WriteLine("  3. Instanciar GptMini con GptMiniConfig");
Console.WriteLine("  4. Loop de entrenamiento (Adam, cross-entropy, checkpoints .pt)");
Console.WriteLine("  5. Guardar pesos entrenados para que la Api los cargue");
Console.WriteLine();

var config = new GptMiniConfig();
Console.WriteLine($"Config inicial -> capas: {config.NumLayers}, dim: {config.EmbeddingDim}, " +
                   $"heads: {config.NumHeads}, contexto: {config.ContextLength} tokens");

// var model = new GptMini(config); // se activa cuando tengamos datos tokenizados
