using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace CorrientesIA.Training.Model;

// Arquitectura decoder-only tipo GPT, escrita a mano con TorchSharp
// (nada de wrappers de otro lenguaje: autodiff y capas son las de libtorch
// pero el grafo de la red lo definimos nosotros aca).
public class GptMini : Module<Tensor, Tensor>
{
    private readonly GptMiniConfig _cfg;
    private readonly Embedding _tokenEmbedding;
    private readonly Embedding _positionEmbedding;
    private readonly ModuleList<TransformerBlock> _blocks;
    private readonly LayerNorm _lnFinal;
    private readonly Linear _head;

    public GptMini(GptMiniConfig cfg) : base(nameof(GptMini))
    {
        _cfg = cfg;

        _tokenEmbedding = Embedding(cfg.VocabSize, cfg.EmbeddingDim);
        _positionEmbedding = Embedding(cfg.ContextLength, cfg.EmbeddingDim);

        _blocks = new ModuleList<TransformerBlock>();
        for (int i = 0; i < cfg.NumLayers; i++)
            _blocks.Add(new TransformerBlock(cfg));

        _lnFinal = LayerNorm(cfg.EmbeddingDim);
        _head = Linear(cfg.EmbeddingDim, cfg.VocabSize, hasBias: false);

        RegisterComponents();
    }

    public override Tensor forward(Tensor idx)
    {
        var (batch, seqLen) = (idx.shape[0], idx.shape[1]);

        var positions = arange(seqLen, device: idx.device).unsqueeze(0);
        var x = _tokenEmbedding.forward(idx) + _positionEmbedding.forward(positions);

        foreach (var block in _blocks)
            x = block.forward(x);

        x = _lnFinal.forward(x);
        var logits = _head.forward(x); // (batch, seqLen, vocabSize)
        return logits;
    }

    /// <summary>
    /// Generacion autoregresiva: parte de promptIds y va agregando un token
    /// por vez, muestreando de la distribucion de probabilidad (sampling con
    /// temperatura), hasta maxNewTokens o hasta encontrar el token EOS.
    /// </summary>
    public long[] Generate(long[] promptIds, int maxNewTokens, int contextLength, double temperature = 0.8, long? eosId = null)
    {
        eval();
        var ids = promptIds.ToList();

        using (no_grad())
        {
            for (int i = 0; i < maxNewTokens; i++)
            {
                var contexto = ids.Skip(Math.Max(0, ids.Count - contextLength)).ToArray();
                var input = tensor(contexto).unsqueeze(0); // (1, seqLen)

                var logits = forward(input); // (1, seqLen, vocabSize)
                var seqLen = (int)logits.shape[1];
                var ultimoLogit = logits.select(1, seqLen - 1).squeeze(0); // (vocabSize)

                var probs = nn.functional.softmax(ultimoLogit / temperature, dim: 0);
                var siguiente = multinomial(probs, 1).item<long>();

                ids.Add(siguiente);
                if (eosId.HasValue && siguiente == eosId.Value) break;
            }
        }

        return ids.ToArray();
    }
}

// Bloque transformer estandar: self-attention causal + MLP, con residuales y layernorm.
public class TransformerBlock : Module<Tensor, Tensor>
{
    private readonly LayerNorm _ln1;
    private readonly MultiheadAttention _attn;
    private readonly LayerNorm _ln2;
    private readonly Sequential _mlp;
    private readonly int _numHeads;

    public TransformerBlock(GptMiniConfig cfg) : base(nameof(TransformerBlock))
    {
        _numHeads = cfg.NumHeads;
        _ln1 = LayerNorm(cfg.EmbeddingDim);
        _attn = MultiheadAttention(cfg.EmbeddingDim, cfg.NumHeads, dropout: cfg.Dropout, batchFirst: true);
        _ln2 = LayerNorm(cfg.EmbeddingDim);
        _mlp = Sequential(
            Linear(cfg.EmbeddingDim, 4 * cfg.EmbeddingDim),
            GELU(),
            Linear(4 * cfg.EmbeddingDim, cfg.EmbeddingDim),
            Dropout(cfg.Dropout)
        );
        RegisterComponents();
    }

    public override Tensor forward(Tensor x)
    {
        var seqLen = (int)x.shape[1];
        // mascara causal: cada posicion solo puede atender a las anteriores (y a si misma)
        var causalMask = torch.triu(torch.ones(seqLen, seqLen), diagonal: 1).to_type(ScalarType.Bool);

        var normed = _ln1.forward(x);
        var (attnOut, _) = _attn.forward(normed, normed, normed, attn_mask: causalMask);
        x = x + attnOut;

        x = x + _mlp.forward(_ln2.forward(x));
        return x;
    }
}
