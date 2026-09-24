namespace CorrientesIA.Training.Model;

// Hiperparametros del GPT-mini. Valores de arranque pensados para
// entrenamiento en CPU local con un corpus chico/mediano.
public class GptMiniConfig
{
    public int VocabSize { get; set; } = 8000;
    public int ContextLength { get; set; } = 128;   // tokens de contexto
    public int EmbeddingDim { get; set; } = 192;     // dimension de embedding
    public int NumLayers { get; set; } = 4;          // bloques transformer
    public int NumHeads { get; set; } = 4;           // heads de atencion
    public double Dropout { get; set; } = 0.1;
    public int BatchSize { get; set; } = 32;
    public double LearningRate { get; set; } = 3e-4;
    public int Epochs { get; set; } = 20;
}
