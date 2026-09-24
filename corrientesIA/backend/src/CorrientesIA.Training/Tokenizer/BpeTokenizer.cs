using System.Text;
using System.Text.Json;

namespace CorrientesIA.Training.Tokenizer;

public class BpeTokenizer
{
    public Dictionary<string, int> Vocab { get; private set; } = new();
    public List<(string, string)> Merges { get; private set; } = new();

    public const string PadToken = "<pad>";
    public const string UnkToken = "<unk>";
    public const string BosToken = "<bos>";
    public const string EosToken = "<eos>";

    private Dictionary<string, int> _tokenToId = new();
    private Dictionary<int, string> _idToToken = new();

    public BpeTokenizer()
    {
        AddToken(PadToken);
        AddToken(UnkToken);
        AddToken(BosToken);
        AddToken(EosToken);
    }

    private void AddToken(string token)
    {
        if (Vocab.ContainsKey(token))
            return;

        var id = Vocab.Count;
        Vocab[token] = id;
    }

    public void Train(IEnumerable<string> corpus, int vocabSize = 8000)
    {
        Vocab.Clear();
        Merges.Clear();

        AddToken(PadToken);
        AddToken(UnkToken);
        AddToken(BosToken);
        AddToken(EosToken);

        var textos = corpus
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        // Tokenización inicial sencilla: palabras + puntuación.
        var frecuencias = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var texto in textos)
        {
            foreach (var token in SepararTokens(texto))
            {
                if (!frecuencias.TryAdd(token, 1))
                    frecuencias[token]++;
            }
        }

        foreach (var token in frecuencias
                     .OrderByDescending(x => x.Value)
                     .ThenBy(x => x.Key)
                     .Select(x => x.Key))
        {
            if (Vocab.Count >= vocabSize)
                break;

            AddToken(token);
        }

        ReconstruirIndices();
    }

    public int[] Encode(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<int>();

        if (_tokenToId.Count == 0)
            ReconstruirIndices();

        var resultado = new List<int>();

        foreach (var token in SepararTokens(text))
        {
            if (_tokenToId.TryGetValue(token, out var id))
            {
                resultado.Add(id);
            }
            else
            {
                // Para tokens desconocidos usamos caracteres individuales.
                foreach (var caracter in token)
                {
                    var s = caracter.ToString();

                    if (_tokenToId.TryGetValue(s, out var charId))
                        resultado.Add(charId);
                    else
                        resultado.Add(Vocab[UnkToken]);
                }
            }
        }

        return resultado.ToArray();
    }

    public string Decode(int[] tokenIds)
    {
        if (_idToToken.Count == 0)
            ReconstruirIndices();

        var tokens = new List<string>();

        foreach (var id in tokenIds)
        {
            if (_idToToken.TryGetValue(id, out var token))
            {
                if (token is PadToken or BosToken or EosToken)
                    continue;

                if (token == UnkToken)
                {
                    tokens.Add("�");
                    continue;
                }

                tokens.Add(token);
            }
        }

        return ReconstruirTexto(tokens);
    }

    public void Save(string path)
    {
        ReconstruirIndices();

        var data = new TokenizerData
        {
            Vocab = Vocab,
            Merges = Merges
                .Select(x => new MergeData
                {
                    Left = x.Item1,
                    Right = x.Item2
                })
                .ToList()
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        var json = JsonSerializer.Serialize(data, options);

        Directory.CreateDirectory(
            Path.GetDirectoryName(Path.GetFullPath(path))!);

        File.WriteAllText(path, json, Encoding.UTF8);
    }

    public static BpeTokenizer Load(string path)
    {
        var json = File.ReadAllText(path);

        var data = JsonSerializer.Deserialize<TokenizerData>(json)
                   ?? throw new InvalidOperationException(
                       "No se pudo cargar el tokenizer.");

        var tokenizer = new BpeTokenizer();

        tokenizer.Vocab = data.Vocab
            ?? new Dictionary<string, int>();

        tokenizer.Merges = (data.Merges ?? new List<MergeData>())
            .Select(x => (x.Left, x.Right))
            .ToList();

        tokenizer.ReconstruirIndices();

        return tokenizer;
    }

    private void ReconstruirIndices()
    {
        _tokenToId = new Dictionary<string, int>(
            Vocab,
            StringComparer.Ordinal);

        _idToToken = Vocab.ToDictionary(
            x => x.Value,
            x => x.Key);
    }

    private static IEnumerable<string> SepararTokens(string texto)
    {
        var resultado = new List<string>();
        var actual = new StringBuilder();

        foreach (var c in texto)
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c >= 128)
            {
                actual.Append(char.ToLowerInvariant(c));
            }
            else
            {
                if (actual.Length > 0)
                {
                    resultado.Add(actual.ToString());
                    actual.Clear();
                }

                if (!char.IsWhiteSpace(c))
                    resultado.Add(c.ToString());
            }
        }

        if (actual.Length > 0)
            resultado.Add(actual.ToString());

        return resultado;
    }

    private static string ReconstruirTexto(IEnumerable<string> tokens)
    {
        var sb = new StringBuilder();

        foreach (var token in tokens)
        {
            if (sb.Length == 0)
            {
                sb.Append(token);
                continue;
            }

            var ultimo = sb[^1];

            if (EsPuntuacion(token) ||
                token == ")" ||
                token == "]" ||
                token == "}" ||
                token == "%")
            {
                sb.Append(token);
            }
            else if (ultimo is '(' or '[' or '{')
            {
                sb.Append(token);
            }
            else
            {
                sb.Append(' ');
                sb.Append(token);
            }
        }

        return sb.ToString();
    }

    private static bool EsPuntuacion(string token)
    {
        return token.Length == 1 &&
               char.IsPunctuation(token[0]);
    }

    private class TokenizerData
    {
        public Dictionary<string, int>? Vocab { get; set; }
        public List<MergeData>? Merges { get; set; }
    }

    private class MergeData
    {
        public string Left { get; set; } = "";
        public string Right { get; set; } = "";
    }
}
