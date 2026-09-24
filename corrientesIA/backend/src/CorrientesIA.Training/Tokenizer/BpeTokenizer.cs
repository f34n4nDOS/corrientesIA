using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CorrientesIA.Training.Tokenizer;

// Tokenizador BPE (Byte Pair Encoding) clasico, tipo Sennrich et al. (2015),
// entrenado desde cero sobre el corpus propio de Corrientes. Nada de
// tokenizadores pre-entrenados de terceros: el vocabulario lo aprende
// este algoritmo a partir de los textos que junta el scraper.
public class BpeTokenizer
{
    public const string PadToken = "<pad>";
    public const string UnkToken = "<unk>";
    public const string BosToken = "<bos>";
    public const string EosToken = "<eos>";
    private const string EndOfWord = "</w>"; // marca el final de cada palabra dentro de un simbolo

    public Dictionary<string, int> Vocab { get; private set; } = new();
    public List<(string A, string B)> Merges { get; private set; } = new();

    // rank de cada merge (indice en el que se aprendio): menor rank = se aplica primero al codificar
    private Dictionary<(string, string), int> _mergeRank = new();
    private static readonly Regex PalabraRegex = new(@"\w+|[^\w\s]", RegexOptions.Compiled);

    public BpeTokenizer()
    {
        Vocab[PadToken] = 0;
        Vocab[UnkToken] = 1;
        Vocab[BosToken] = 2;
        Vocab[EosToken] = 3;
    }

    /// <summary>
    /// Entrena el vocabulario BPE a partir de una lista de textos crudos.
    /// vocabSize recomendado para este dominio acotado: 8000-16000.
    /// </summary>
    public void Train(IEnumerable<string> corpus, int vocabSize = 8000)
    {
        // 1. Pre-tokenizar: contar frecuencia de cada palabra en todo el corpus.
        //    Cada palabra se representa como secuencia de simbolos separados por
        //    espacio, empezando por caracteres sueltos + marca de fin de palabra.
        //    ej: "correntino" -> "c o r r e n t i n o </w>"
        var frecuenciaPalabras = new Dictionary<string, int>();

        foreach (var texto in corpus)
        {
            foreach (Match m in PalabraRegex.Matches(texto.ToLowerInvariant()))
            {
                var palabra = string.Join(' ', m.Value.Select(c => c.ToString())) + " " + EndOfWord;
                frecuenciaPalabras.TryGetValue(palabra, out var cnt);
                frecuenciaPalabras[palabra] = cnt + 1;
            }
        }

        if (frecuenciaPalabras.Count == 0)
            throw new InvalidOperationException("El corpus esta vacio, no hay nada que tokenizar.");

        // 2. Vocabulario inicial = todos los caracteres unicos + tokens especiales
        var simbolosIniciales = frecuenciaPalabras.Keys
            .SelectMany(p => p.Split(' '))
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal);

        foreach (var s in simbolosIniciales)
            if (!Vocab.ContainsKey(s))
                Vocab[s] = Vocab.Count;

        // 3. Bucle de merges: en cada iteracion, fusionar el par de simbolos
        //    adyacentes mas frecuente en todo el corpus, hasta llegar a vocabSize.
        Merges.Clear();
        while (Vocab.Count < vocabSize)
        {
            var pares = ContarPares(frecuenciaPalabras);
            if (pares.Count == 0) break; // ya no quedan pares para fusionar

            var mejorPar = pares.OrderByDescending(p => p.Value).First().Key;

            frecuenciaPalabras = AplicarMerge(frecuenciaPalabras, mejorPar);

            Merges.Add(mejorPar);
            var nuevoSimbolo = mejorPar.Item1 + mejorPar.Item2;
            if (!Vocab.ContainsKey(nuevoSimbolo))
                Vocab[nuevoSimbolo] = Vocab.Count;
        }

        _mergeRank = Merges
            .Select((par, idx) => (par, idx))
            .ToDictionary(x => x.par, x => x.idx);
    }

    private static Dictionary<(string, string), int> ContarPares(Dictionary<string, int> frecuenciaPalabras)
    {
        var pares = new Dictionary<(string, string), int>();

        foreach (var (palabra, frecuencia) in frecuenciaPalabras)
        {
            var simbolos = palabra.Split(' ');
            for (int i = 0; i < simbolos.Length - 1; i++)
            {
                var par = (simbolos[i], simbolos[i + 1]);
                pares.TryGetValue(par, out var cnt);
                pares[par] = cnt + frecuencia;
            }
        }

        return pares;
    }

    private static Dictionary<string, int> AplicarMerge(
        Dictionary<string, int> frecuenciaPalabras, (string A, string B) par)
    {
        var resultado = new Dictionary<string, int>();
        var patron = Regex.Escape(par.A) + " " + Regex.Escape(par.B);
        var reemplazo = par.A + par.B;

        foreach (var (palabra, frecuencia) in frecuenciaPalabras)
        {
            var nuevaPalabra = Regex.Replace(palabra, patron, reemplazo);
            resultado.TryGetValue(nuevaPalabra, out var cnt);
            resultado[nuevaPalabra] = cnt + frecuencia;
        }

        return resultado;
    }

    /// <summary>Codifica texto libre en una secuencia de ids de tokens.</summary>
    public int[] Encode(string text)
    {
        if (_mergeRank.Count == 0 && Merges.Count > 0)
            _mergeRank = Merges.Select((par, idx) => (par, idx)).ToDictionary(x => x.par, x => x.idx);

        var ids = new List<int>();

        foreach (Match m in PalabraRegex.Matches(text.ToLowerInvariant()))
        {
            var simbolos = m.Value.Select(c => c.ToString()).ToList();
            simbolos.Add(EndOfWord);

            // aplicar merges en el orden en que se aprendieron, hasta que no quede ninguno aplicable
            while (simbolos.Count > 1)
            {
                var mejorRank = int.MaxValue;
                var mejorIdx = -1;

                for (int i = 0; i < simbolos.Count - 1; i++)
                {
                    if (_mergeRank.TryGetValue((simbolos[i], simbolos[i + 1]), out var rank) && rank < mejorRank)
                    {
                        mejorRank = rank;
                        mejorIdx = i;
                    }
                }

                if (mejorIdx == -1) break; // ningun par restante tiene merge aprendido

                var fusionado = simbolos[mejorIdx] + simbolos[mejorIdx + 1];
                simbolos[mejorIdx] = fusionado;
                simbolos.RemoveAt(mejorIdx + 1);
            }

            foreach (var s in simbolos)
                ids.Add(Vocab.TryGetValue(s, out var id) ? id : Vocab[UnkToken]);
        }

        return ids.ToArray();
    }

    /// <summary>Decodifica una secuencia de ids de vuelta a texto legible.</summary>
    public string Decode(int[] tokenIds)
    {
        var idAToken = Vocab.ToDictionary(kv => kv.Value, kv => kv.Key);
        var sb = new StringBuilder();

        foreach (var id in tokenIds)
        {
            if (!idAToken.TryGetValue(id, out var token)) continue;
            if (token is PadToken or BosToken or EosToken) continue;

            if (token.EndsWith(EndOfWord))
            {
                sb.Append(token[..^EndOfWord.Length]);
                sb.Append(' ');
            }
            else
            {
                sb.Append(token);
            }
        }

        return sb.ToString().Trim();
    }

    private class TokenizerData
    {
        public Dictionary<string, int> Vocab { get; set; } = new();
        public List<string[]> Merges { get; set; } = new();
    }

    public void Save(string path)
    {
        var data = new TokenizerData
        {
            Vocab = Vocab,
            Merges = Merges.Select(m => new[] { m.A, m.B }).ToList()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static BpeTokenizer Load(string path)
    {
        var data = JsonSerializer.Deserialize<TokenizerData>(File.ReadAllText(path))
                   ?? throw new InvalidOperationException("No se pudo leer el tokenizador.");

        var tok = new BpeTokenizer
        {
            Vocab = data.Vocab,
            Merges = data.Merges.Select(m => (m[0], m[1])).ToList()
        };
        tok._mergeRank = tok.Merges.Select((par, idx) => (par, idx)).ToDictionary(x => x.par, x => x.idx);
        return tok;
    }
}
