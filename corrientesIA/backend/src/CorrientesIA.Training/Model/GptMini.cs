using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace CorrientesIA.Training.Model;

// Arquitectura decoder-only tipo GPT, escrita a mano con TorchSharp.
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

        _tokenEmbedding = Embedding(
            cfg.VocabSize,
            cfg.EmbeddingDim);

        _positionEmbedding = Embedding(
            cfg.ContextLength,
            cfg.EmbeddingDim);

        _blocks = new ModuleList<TransformerBlock>();

        for (int i = 0; i < cfg.NumLayers; i++)
        {
            _blocks.Add(new TransformerBlock(cfg));
        }

        _lnFinal = LayerNorm(cfg.EmbeddingDim);

        _head = Linear(
            cfg.EmbeddingDim,
            cfg.VocabSize,
            hasBias: false);

        RegisterComponents();
    }

    public override Tensor forward(Tensor idx)
    {
        var seqLen = (int)idx.shape[1];

        var positions = arange(
            seqLen,
            device: idx.device)
            .unsqueeze(0);

        var x =
            _tokenEmbedding.forward(idx) +
            _positionEmbedding.forward(positions);

        foreach (var block in _blocks)
        {
            x = block.forward(x);
        }

        x = _lnFinal.forward(x);

        var logits = _head.forward(x);

        return logits;
    }
}


// Bloque Transformer:
// self-attention causal + MLP,
// con conexiones residuales y LayerNorm.
public class TransformerBlock : Module<Tensor, Tensor>
{
    private readonly LayerNorm _ln1;
    private readonly MultiheadAttention _attn;
    private readonly LayerNorm _ln2;
    private readonly Sequential _mlp;

    public TransformerBlock(GptMiniConfig cfg)
        : base(nameof(TransformerBlock))
    {
        _ln1 = LayerNorm(cfg.EmbeddingDim);

        _attn = MultiheadAttention(
            cfg.EmbeddingDim,
            cfg.NumHeads,
            dropout: cfg.Dropout);

        _ln2 = LayerNorm(cfg.EmbeddingDim);

        _mlp = Sequential(
            Linear(
                cfg.EmbeddingDim,
                4 * cfg.EmbeddingDim),

            GELU(),

            Linear(
                4 * cfg.EmbeddingDim,
                cfg.EmbeddingDim),

            Dropout(cfg.Dropout)
        );

        RegisterComponents();
    }

    public override Tensor forward(Tensor x)
    {
        // Entrada:
        // [batch, sequence, embedding]
        var seqLen = (int)x.shape[1];

        // Normalización.
        var normed = _ln1.forward(x);

        // TorchSharp MultiheadAttention utiliza:
        // [sequence, batch, embedding]
        var normedSeqFirst = normed.transpose(0, 1);

        // Máscara causal:
        // true = posición bloqueada.
        using var causalMask = torch.triu(
            torch.ones(
                seqLen,
                seqLen,
                dtype: ScalarType.Bool),
            diagonal: 1);

        var attentionResult = _attn.forward(
            normedSeqFirst,
            normedSeqFirst,
            normedSeqFirst,
            key_padding_mask: null,
            need_weights: false,
            attn_mask: causalMask);

        var attnOutSeqFirst = attentionResult.Item1;

        // Volvemos a:
        // [batch, sequence, embedding]
        var attnOut = attnOutSeqFirst.transpose(0, 1);

        // Residual attention.
        x = x + attnOut;

        // MLP + segundo residual.
        x = x + _mlp.forward(
            _ln2.forward(x));

        return x;
    }
}
