namespace RevisionsEditor.Core.Diagrams;

/// <summary>Starting points for a new diagram: a plain pipeline and three network figures.</summary>
public static class DiagramTemplates
{
    public sealed record Template(string Name, string Description, string Source);

    public static IReadOnlyList<Template> All { get; } =
    [
        new("Pipeline", "Input, a few stages and an output, joined by arrows.", """
            # A pipeline: nodes go left to right in the order they are written.
            tensor in    "Input" "$3 \times H \times W$" thin
            block  enc   "Encoder" "ConvNeXt-L"
            block  head  "Head" "$1024 \to 256$"
            tensor out   "Output" "$256 \times \frac{H}{4} \times \frac{W}{4}$"

            in -> enc -> head -> out
            """),

        new("Feature extraction network", "A backbone, a feature stack, refiner stages and a side branch (like a DINOv3 descriptor network).", """
            # Feature extraction network
            tensor in    "Input" "$3 \times H \times W$" thin
            block  dino  "DINOv3" "ConvNeXt-L"
            tensor hyp   "Hyperstack" "$2880 \times \frac{H}{4} \times \frac{W}{4}$" deep
            block  ref1  "Refiner" "$2 \times (C = 2880)$"
            block  pca   "PCA Matrix" "$W \in \mathbb{R}^{1024 \times 2880}$"
            block  lin   "Linear" "$1024 \times 1024$"
            block  ref2  "Refiner" "$2 \times (C = 1024)$"
            block  codec "int8 Codec" "Affine Absmax"
            tensor out   "Descriptor" "$1024 \times \frac{H}{4} \times \frac{W}{4}$"
            # A side branch in the row below, with a stop-gradient (dashed) arrow into it.
            block  oos   "OOS Head" "$1024 \to 2880$" below=codec dashed shaded
            text   sig   "OOS Signal" "$\|x - \hat{x}\|^2$" after=oos bold

            in -> dino -> hyp -> ref1 -> pca -> lin -> ref2 -> codec -> out
            ref2 --> oos label="$\nabla z = 0$"
            oos -> sig
            """),

        new("Residual block", "Layers inside a residual connection: x + γf(x).", """
            # Residual refiner block
            text   x     "$x$"
            block  dw    "$\mathrm{Conv2D}_{\mathrm{dw}}$" "$7 \times 7,\ \|w_c\|_1 \le 1$" plain
            block  pw1   "$\mathrm{Conv2D}_{1 \times 1}$" "$C \to 512$ (SN)" plain
            block  gs    "GroupSort-2" "Pairwise Sort"
            block  pw2   "$\mathrm{Conv2D}_{1 \times 1}$" "$512 \to C$ (SN)" plain
            block  gamma "$\times \gamma$" "$\gamma \in [-0.9, 0.9]$" plain
            op     add   add
            text   out   "$x + \gamma f(x)$"

            x -> dw -> pw1 -> gs -> pw2 -> gamma -> add -> out
            # The skip connection, routed over the row.
            x --> add over
            """),

        new("Two-branch fusion", "Two descriptors fused, a residual feed-forward layer and a matcher (like an adaptor network).", """
            # Adaptor network
            tensor dino  "DINOv3" "1024-D"
            block  s1    "Stage 1" "Linear+LN+GELU" caption="$1024 \to 768$"
            block  s2    "Stage 2" "Linear+LN+GELU" caption="$768 \to 512$"
            block  s3    "Stage 3" "Linear+LN+GELU" caption="$512 \to 384$"
            block  s4    "Stage 4" "Linear+LN+GELU" caption="$384 \to 256$"
            tensor dedo  "DeDoDeV2" "256-D" below=dino
            # span=2: centred across both rows, where the two branches meet.
            block  cat   "Concat" "$\mathbf{v}_1 \parallel \mathbf{v}_2$" caption="512-D" span=2 shaded
            block  ln    "LayerNorm" "Dim: 512"
            block  l1    "Linear" "$512 \to 1024$"
            block  gelu  "GELU"
            block  l2    "Linear" "$1024 \to 512$"
            op     add   add
            block  proj  "Linear Proj." "$512 \to 256$" caption="256-D"
            tensor desc  "Descriptor" "256-D"
            block  lg    "LightGlue" "Matcher" caption="End-to-End" shaded

            group ladder "4-Stage Compression Ladder ($1024 \to 256$)" s1 s2 s3 s4 dotted
            group ffn "Non-Linear Residual Feed-Forward Layer" ln l1 gelu l2 add shaded

            dino -> s1 -> s2 -> s3 -> s4 -> cat
            dedo -> cat label="Pass-through (256-D)"
            cat -> ln -> l1 -> gelu -> l2 -> add -> proj -> desc -> lg
            cat --> add under label="$\mathbf{x}$ (Identity)"
            """)
    ];
}
