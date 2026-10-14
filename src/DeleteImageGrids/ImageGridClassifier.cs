using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;

namespace Cove.DeleteImageGrids;

public sealed class ImageGridClassifier : IDisposable
{
    public const int InputSize = 384;
    private const int ResizeSize = 438;
    public const string LegacyCenterCrop = "legacy_center_crop_v1";
    public const string LetterboxImageNet = "letterbox_imagenet_v1";
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];
    private readonly object _sessionGate = new();
    private InferenceSession _session;
    private string _activeModelPath;
    private string _activeModelVersion;
    private string _activePreprocessing;

    public string ActiveModelPath { get { lock (_sessionGate) return _activeModelPath; } }
    public string ActiveModelVersion { get { lock (_sessionGate) return _activeModelVersion; } }
    public string ActivePreprocessing { get { lock (_sessionGate) return _activePreprocessing; } }

    public ImageGridClassifier(string modelPath, string modelVersion = "bundled")
    {
        _session = CreateSession(modelPath);
        _activeModelPath = Path.GetFullPath(modelPath);
        _activeModelVersion = modelVersion;
        _activePreprocessing = ReadPreprocessing(modelPath);
    }

    public async Task<float> ClassifyGridConfidenceAsync(Stream source, CancellationToken ct)
    {
        using var image = await LoadImageAsync(source, ct);
        string preprocessing;
        lock (_sessionGate) preprocessing = _activePreprocessing;
        if (preprocessing == LetterboxImageNet)
        {
            image.Mutate(context => context.Resize(new ResizeOptions
            {
                Size = new Size(InputSize, InputSize),
                Mode = ResizeMode.Pad,
                Position = AnchorPositionMode.Center,
                Sampler = KnownResamplers.Triangle,
                PadColor = new Color(new Rgb24(124, 116, 104))
            }));
        }
        else
        {
            var width = image.Width <= image.Height
                ? ResizeSize
                : (int)Math.Round(image.Width * (ResizeSize / (double)image.Height));
            var height = image.Width <= image.Height
                ? (int)Math.Round(image.Height * (ResizeSize / (double)image.Width))
                : ResizeSize;
            image.Mutate(context => context
                .Resize(width, height)
                .Crop(new Rectangle((width - InputSize) / 2, (height - InputSize) / 2, InputSize, InputSize)));
        }

        var tensor = new DenseTensor<float>([1, 3, InputSize, InputSize]);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < InputSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < InputSize; x++)
                {
                    var pixel = row[x];
                    tensor[0, 0, y, x] = (pixel.R / 255f - Mean[0]) / Std[0];
                    tensor[0, 1, y, x] = (pixel.G / 255f - Mean[1]) / Std[1];
                    tensor[0, 2, y, x] = (pixel.B / 255f - Mean[2]) / Std[2];
                }
            }
        });

        ct.ThrowIfCancellationRequested();
        var input = NamedOnnxValue.CreateFromTensor("input", tensor);
        float[] logits;
        lock (_sessionGate)
        {
            using var output = _session.Run([input]);
            logits = output.First().AsEnumerable<float>().Take(2).ToArray();
        }
        if (logits.Length != 2) throw new InvalidDataException("The classifier returned an unexpected output shape.");
        var max = Math.Max(logits[0], logits[1]);
        var grid = Math.Exp(logits[0] - max);
        var notGrid = Math.Exp(logits[1] - max);
        return (float)(grid / (grid + notGrid));
    }

    internal static async Task<Image<Rgb24>> LoadImageAsync(Stream source, CancellationToken ct)
    {
        var start = source.CanSeek ? source.Position : 0;
        try { return await Image.LoadAsync<Rgb24>(source, ct); }
        catch (InvalidImageContentException) when (source.CanSeek)
        {
            source.Position = start;
            var signature = new byte[8];
            var count = await source.ReadAsync(signature, ct);
            source.Position = start;
            if (count != 8 || !signature.AsSpan().SequenceEqual(new byte[] {137, 80, 78, 71, 13, 10, 26, 10})) throw;
            return await PngDecoder.Instance.DecodeAsync<Rgb24>(new PngDecoderOptions
            {
                PngCrcChunkHandling = PngCrcChunkHandling.IgnoreAll
            }, source, ct);
        }
    }

    public void Reload(string modelPath, string? modelVersion = null)
    {
        var replacement = CreateSession(modelPath);
        var preprocessing = ReadPreprocessing(modelPath);
        InferenceSession previous;
        lock (_sessionGate)
        {
            previous = _session;
            _session = replacement;
            _activeModelPath = Path.GetFullPath(modelPath);
            _activePreprocessing = preprocessing;
            if (!string.IsNullOrWhiteSpace(modelVersion)) _activeModelVersion = modelVersion;
        }
        previous.Dispose();
    }

    private static InferenceSession CreateSession(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("The image-grid model is missing.", modelPath);
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount - 1)
        };
        return new InferenceSession(modelPath, options);
    }

    private static string ReadPreprocessing(string modelPath)
    {
        var sidecar = Path.ChangeExtension(modelPath, ".preprocess");
        var value = File.Exists(sidecar) ? File.ReadAllText(sidecar).Trim() : LegacyCenterCrop;
        return value is LegacyCenterCrop or LetterboxImageNet
            ? value
            : throw new InvalidDataException($"Unsupported model preprocessing version: {value}");
    }

    public void Dispose()
    {
        lock (_sessionGate) _session.Dispose();
    }
}
