namespace CorrientesIA.Training.Tokenizer;

// Tokenizador BPE (Byte Pair Encoding) entrenado sobre el corpus propio.
// Fase 1: solo el esqueleto. La logica real de "aprender merges" se completa
// cuando tengamos el corpus (scraper) listo.
public class BpeTokenizer
{
    public Dictionary<string, int> Vocab { get; private set; } = new();
    public List<(string, string)> Merges { get; private set; } = new();

    public const string PadToken = "<pad>";
    public const string UnkToken = "<unk>";
    public const string BosToken = "<bos>";
    public const string EosToken = "<eos>";

    public BpeTokenizer()
    {
        // vocabulario especial reservado, se completa al entrenar
        Vocab[PadToken] = 0;
        Vocab[UnkToken] = 1;
        Vocab[BosToken] = 2;
        Vocab[EosToken] = 3;
    }

    /// Entrena el vocabulario BPE a partir de una lista de textos crudos.
    /// vocabSize recomendado para este dominio acotado: 8000-16000.
    public void Train(IEnumerable<string> corpus, int vocabSize = 8000)
    {
        // TODO: implementar el algoritmo BPE clásico:
        // 1. Partir en caracteres/bytes
        // 2. Contar pares adyacentes mas frecuentes
        // 3. Fusionar el par mas frecuente en un nuevo token
        // 4. Repetir hasta alcanzar vocabSize
        throw new NotImplementedException("Se implementa en la fase de armado del corpus.");
    }

    public int[] Encode(string text)
    {
        throw new NotImplementedException();
    }

    public string Decode(int[] tokenIds)
    {
        throw new NotImplementedException();
    }

    public void Save(string path) => throw new NotImplementedException();
    public static BpeTokenizer Load(string path) => throw new NotImplementedException();
}
