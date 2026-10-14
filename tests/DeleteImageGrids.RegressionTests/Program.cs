using Cove.DeleteImageGrids;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

var failures = new List<string>();
Check(PathScope.Contains("C:/library", "C:/library"), "root path matches itself");
Check(PathScope.Contains("C:/library", "C:/library/grids/a.jpg"), "descendant path matches");
Check(!PathScope.Contains("C:/library", "C:/library-other/a.jpg"), "sibling prefix is not a descendant");
Check(PathScope.Contains("c:\\LIBRARY", "C:/library/grids/a.jpg"), "matching is slash and case insensitive");
Check(PathScope.Includes("C:/library/a.jpg", "all", []), "all includes every path");
Check(PathScope.Includes("C:/library/grids/a.jpg", "include", ["C:/library/grids"]), "include accepts descendants");
Check(!PathScope.Includes("C:/library/plain/a.jpg", "include", ["C:/library/grids"]), "include rejects paths outside selection");
Check(!PathScope.Includes("C:/library/grids/a.jpg", "exclude", ["C:/library/grids"]), "exclude rejects descendants");
Check(PathScope.Includes("C:/library/plain/a.jpg", "exclude", ["C:/library/grids"]), "exclude accepts paths outside selection");

var normalized = GridSettings.Normalize(new GridSettings("INCLUDE", [" C:\\library\\grids\\ ", "C:/library/grids"]));
Check(normalized.FolderMode == "include", "folder mode normalizes");
Check(normalized.Paths?.Count == 1 && normalized.Paths[0].EndsWith("/library/grids", StringComparison.OrdinalIgnoreCase), "paths normalize and deduplicate");
Check(GridSettings.Normalize(new GridSettings("exclude", [])).FolderMode == "all", "empty selective scope falls back to all");
Check(GridSettings.Normalize(null).PublishMetadata, "Cove metadata publication defaults on");
Check(!GridSettings.Normalize(new GridSettings(PublishMetadata: false)).PublishMetadata, "metadata publication can be disabled");
Check(!GridSettings.Normalize(null).ShowResults, "classification result display defaults off");
Check(GridSettings.Normalize(new GridSettings(ShowResults: true)).ShowResults, "classification result display can be enabled");
Check(GridMetadataValues.ToStoredPercent(.98449f) == 98.4m, "published grid probability rounds to one tenth of a percent");

var resultStoreFolder = Path.Combine(Path.GetTempPath(), $"igd-results-{Guid.NewGuid():N}");
Directory.CreateDirectory(resultStoreFolder);
try
{
    using var store = new GridResultStore(resultStoreFolder);
    var firstRun = await store.CreateRunAsync(new GridSettings(), "model-a", CancellationToken.None);
    await store.SetCandidateCountAsync(firstRun.Id, 257, CancellationToken.None);
    for (var id = 1; id <= 257; id++)
    {
        var confidence = id / 258f;
        await store.AddResultAsync(firstRun.Id, new GridMatch(id, $"C:/library/{id}.jpg", confidence,
            confidence >= .5f, [new GridFileInfo(id, $"C:/library/{id}.jpg", id, 100, 100, false)], 0, false),
            id, CancellationToken.None);
    }
    await store.CompleteAsync(firstRun.Id, "completed", null, CancellationToken.None);
    var page = await store.GetPageAsync(firstRun.Id, 2, 50, "classified", "model-a", true, CancellationToken.None);
    Check(page.Items.Count == 50 && page.TotalPages == 6, "disk-backed results paginate without loading the full run");
    Check(page.Items.Zip(page.Items.Skip(1)).All(pair => pair.First.GridConfidence >= pair.Second.GridConfidence),
        "disk-backed pages retain confidence order");
    var summaryOnly = await store.GetPageAsync(firstRun.Id, 1, 50, "classified", "model-a", false, CancellationToken.None);
    Check(summaryOnly.Items.Count == 0 && summaryOnly.ClassifiedCount == 257,
        "summary-only runs retain KPIs without loading result rows");
    var selection = await store.GetSelectionBatchAsync(new(new(firstRun.Id, "grids", true, [257]), BatchSize: 32), CancellationToken.None);
    Check(selection.Items.Count <= 32 && selection.Items.All(item => item.ImageId != 257),
        "all-matching selection is bounded and honors manual exceptions");

    var secondRun = await store.CreateRunAsync(new GridSettings(), "model-b", CancellationToken.None);
    await store.MarkRunningAsync(secondRun.Id, CancellationToken.None);
    Check((await store.GetRunAsync(secondRun.Id, CancellationToken.None))?.CandidateCount == -1,
        "streaming runs have an unknown total while running");
    await store.CompleteAsync(secondRun.Id, "cancelled", null, CancellationToken.None);
    Check((await store.GetRunAsync(secondRun.Id, CancellationToken.None))?.Resumable == true,
        "unknown-total runs can resume even before processing the first image");
    await store.MarkRunningAsync(secondRun.Id, CancellationToken.None);
    await store.MarkSkippedAsync(secondRun.Id, 10, CancellationToken.None);
    await store.CompleteAsync(secondRun.Id, "failed", "test failure", CancellationToken.None);
    var partialRun = await store.GetRunAsync(secondRun.Id, CancellationToken.None);
    Check(partialRun is { Resumable: true, ProcessedCount: 1, SkippedCount: 1, LastImageId: 10 },
        "failed streaming runs preserve their cursor and running counts for resume");
    await store.MarkRunningAsync(secondRun.Id, CancellationToken.None);
    await store.CompleteAsync(secondRun.Id, "completed", null, CancellationToken.None);
    Check((await store.GetRunAsync(secondRun.Id, CancellationToken.None)) is { CandidateCount: 1, Resumable: false },
        "completed streaming runs finalize their total including skipped images");
    var thirdRun = await store.CreateRunAsync(new GridSettings(), "model-c", CancellationToken.None);
    await store.MarkRunningAsync(thirdRun.Id, CancellationToken.None);
    await store.CompleteAsync(thirdRun.Id, "completed", null, CancellationToken.None);
    Check((await store.GetRunAsync(thirdRun.Id, CancellationToken.None)) is { CandidateCount: 0, Resumable: false },
        "empty streaming runs complete with a zero total");
    var retained = await store.ListRunsAsync(CancellationToken.None);
    Check(retained.Count == 2 && retained[0].RunId == thirdRun.Id && retained[1].RunId == secondRun.Id,
        "result retention keeps the current and previous completed runs");
    // Keep the store alive: disposing it previously cleared pools and hid this lock.
    var databasePath = Path.Combine(resultStoreFolder, "classification-results.sqlite3");
    using (var exclusive = new FileStream(databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Check(exclusive.Length > 0, "idle result store releases the database handle after cancellation and reads");
    var movedDatabase = databasePath + ".replacement-check";
    File.Move(databasePath, movedDatabase);
    File.Move(movedDatabase, databasePath);
    Check((await store.GetRunAsync(thirdRun.Id, CancellationToken.None))?.Status == "completed",
        "store reopens database after an extension replacement file operation");
}
finally { Directory.Delete(resultStoreFolder, true); }

Check(TrainingOutputParser.TryParseBestValidationAccuracy("Done. Best val acc 0.992. Saved to model.pt", out var legacyAccuracy)
    && Math.Abs(legacyAccuracy - 0.992) < 0.000001, "best validation accuracy ignores sentence punctuation");
Check(!TrainingOutputParser.TryParseBestValidationAccuracy("Done. Best val acc invalid.", out _),
    "malformed best validation accuracy is ignored");
Check(TrainingOutputParser.TryParseCheckpoint("Best checkpoint: epoch 4/10, val acc 0.992000, val loss 0.017000", out var checkpoint)
    && checkpoint.Epoch == 4 && checkpoint.TotalEpochs == 10
    && Math.Abs(checkpoint.Accuracy - 0.992) < 0.000001 && Math.Abs(checkpoint.Loss - 0.017) < 0.000001,
    "structured best checkpoint metrics parse");
Check(TrainingOutputParser.TryParseEpoch("Starting epoch 5/10", out var startingEpoch)
    && startingEpoch.Current == 5 && !startingEpoch.Completed, "starting epoch progress parses");
Check(TrainingOutputParser.TryParseEpoch("Epoch  5/10  train loss 0.015", out var completedEpoch)
    && completedEpoch.Current == 5 && completedEpoch.Completed, "completed epoch progress parses");

var dedupeFolder = Path.Combine(Path.GetTempPath(), $"igd-dedupe-{Guid.NewGuid():N}");
Directory.CreateDirectory(dedupeFolder);
try
{
    const string digest = "abcdef0123456789";
    var existing = Path.Combine(dedupeFolder, $"cove-42-{digest}.jpg");
    File.WriteAllText(existing, "sample");
    Check(TrainingDatasetDeduplication.FindByDigest(dedupeFolder, digest) == existing,
        "repeat training samples resolve to the existing content hash");
    Check(TrainingDatasetDeduplication.FindByDigest(dedupeFolder, "different") is null,
        "new training sample hashes remain addable");
}
finally { Directory.Delete(dedupeFolder, true); }

var baselineMetrics = Metrics(gridTotal: 40, gridCorrect: 38, notGridTotal: 60, falseGrids: 0);
Check(ModelQualityGate.Compare(baselineMetrics, Metrics(40, 39, 60, 0)).Accepted,
    "quality gate accepts zero false grids with improved grid recall");
var unsafeCandidate = ModelQualityGate.Compare(baselineMetrics, Metrics(40, 40, 60, 1));
Check(!unsafeCandidate.Accepted && unsafeCandidate.Message.Contains("marked 1 non-grid images as grids", StringComparison.OrdinalIgnoreCase),
    "quality gate rejects false grids even when grid recall improves");
Check(!ModelQualityGate.Compare(baselineMetrics, Metrics(40, 37, 60, 0)).Accepted,
    "quality gate rejects lower grid recall when false grids tie");
Check(ModelQualityGate.Compare(Metrics(40, 38, 60, 1), Metrics(40, 38, 60, 0)).Accepted,
    "quality gate accepts fewer false grids when recall does not decrease");
Check(!ModelQualityGate.Compare(baselineMetrics, Metrics(40, 38, 60, 0)).Accepted,
    "quality gate archives an exact tie rather than promoting it");

var registryRoot = Path.Combine(Path.GetTempPath(), $"igd-models-{Guid.NewGuid():N}");
var extensionRegistryRoot = Path.Combine(registryRoot, "extension");
var trainingRegistryRoot = Path.Combine(registryRoot, "training");
Directory.CreateDirectory(Path.Combine(extensionRegistryRoot, "artifacts"));
Directory.CreateDirectory(Path.Combine(trainingRegistryRoot, "models", "0.2.8"));
File.WriteAllText(Path.Combine(extensionRegistryRoot, "artifacts", "build-sequence.txt"), "7");
try
{
    Check(ModelVersionRegistry.NextVersion(extensionRegistryRoot, trainingRegistryRoot) == "0.2.9",
        "model versions advance past both packages and rejected model archives");
}
finally { Directory.Delete(registryRoot, true); }

var modelPath = Path.Combine(Environment.CurrentDirectory, "src", "DeleteImageGrids", "model", "image-grid-classifier.onnx");
if (File.Exists(modelPath))
{
    using var classifier = new ImageGridClassifier(modelPath, "test-bundled");
    Check(classifier.ActiveModelVersion == "test-bundled", "classifier exposes its active model version");
    Check(classifier.ActivePreprocessing == ImageGridClassifier.LegacyCenterCrop,
        "models without a preprocessing sidecar retain legacy center crop");
    classifier.Reload(modelPath, "test-reloaded");
    Check(classifier.ActiveModelVersion == "test-reloaded", "classifier updates its model version on hot reload");
    using var image = new Image<Rgb24>(512, 512, new Rgb24(235, 235, 235));
    image.ProcessPixelRows(accessor =>
    {
        for (var y = 0; y < 512; y++)
        {
            var row = accessor.GetRowSpan(y);
            for (var x = 0; x < 512; x++)
                if (x % 128 < 5 || y % 128 < 5) row[x] = new Rgb24(25, 25, 25);
        }
    });
    await using var stream = new MemoryStream();
    await image.SaveAsync(stream, new PngEncoder());
    stream.Position = 0;
    var confidence = await classifier.ClassifyGridConfidenceAsync(stream, CancellationToken.None);
    Check(float.IsFinite(confidence) && confidence is >= 0 and <= 1, "ONNX runtime smoke inference returns a probability");

    var damagedPng = stream.ToArray();
    for (var offset = 8; offset < damagedPng.Length;)
    {
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(damagedPng.AsSpan(offset, 4));
        if (System.Text.Encoding.ASCII.GetString(damagedPng, offset + 4, 4) == "IDAT")
        {
            damagedPng[offset + 8 + length] ^= 1;
            break;
        }
        offset += length + 12;
    }
    using var damagedStream = new MemoryStream(damagedPng);
    var strictRejected = false;
    try { using var strictImage = await Image.LoadAsync<Rgb24>(damagedStream); }
    catch (InvalidImageContentException) { strictRejected = true; }
    Check(strictRejected, "PNG fixture reproduces strict CRC rejection");
    damagedStream.Position = 0;
    using var recoveredPng = await ImageGridClassifier.LoadImageAsync(damagedStream, CancellationToken.None);
    Check(recoveredPng.Width == image.Width && recoveredPng[10, 10].Equals(image[10, 10]),
        "CRC recovery preserves decoded pixels");

    using var jpegStream = new MemoryStream();
    await image.SaveAsJpegAsync(jpegStream);
    using var ffmpegImage = await ImageDecodeRecovery.DecodeAsync(jpegStream, null, CancellationToken.None);
    using var decodedJpeg = await Image.LoadAsync<Rgb24>(ffmpegImage);
    Check(decodedJpeg.Width == 640 && decodedJpeg.Height == 640, "direct FFmpeg recovery handles JPEG sources independently of thumbnail cache");
    using var invalidStream = new MemoryStream(new byte[] { 1, 2, 3, 4 });
    var invalidRejected = false;
    try { using var invalidImage = await ImageDecodeRecovery.DecodeAsync(invalidStream, null, CancellationToken.None); }
    catch (InvalidDataException) { invalidRejected = true; }
    Check(invalidRejected, "unrecoverable input remains a failure rather than a classification");

    var letterboxDirectory = Path.Combine(Path.GetTempPath(), $"igd-letterbox-{Guid.NewGuid():N}");
    Directory.CreateDirectory(letterboxDirectory);
    try
    {
        var letterboxModel = Path.Combine(letterboxDirectory, "image-grid-classifier.onnx");
        File.Copy(modelPath, letterboxModel);
        File.WriteAllText(Path.ChangeExtension(letterboxModel, ".preprocess"), ImageGridClassifier.LetterboxImageNet);
        using var letterboxClassifier = new ImageGridClassifier(letterboxModel, "test-letterbox");
        Check(letterboxClassifier.ActivePreprocessing == ImageGridClassifier.LetterboxImageNet,
            "classifier loads versioned letterbox preprocessing");
        using var landscape = new Image<Rgb24>(800, 200, new Rgb24(180, 90, 30));
        await using var landscapeStream = new MemoryStream();
        await landscape.SaveAsync(landscapeStream, new PngEncoder());
        landscapeStream.Position = 0;
        var letterboxConfidence = await letterboxClassifier.ClassifyGridConfidenceAsync(landscapeStream, CancellationToken.None);
        Check(float.IsFinite(letterboxConfidence) && letterboxConfidence is >= 0 and <= 1,
            "letterboxed landscape inference returns a probability");
    }
    finally { Directory.Delete(letterboxDirectory, true); }
}
else failures.Add("packaged ONNX model exists for inference smoke test");

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures.Select(failure => "FAIL: " + failure)));
    return 1;
}
Console.WriteLine("Delete Image Grids regression tests passed.");
return 0;

void Check(bool condition, string name)
{
    if (!condition) failures.Add(name);
}

ModelEvaluationMetrics Metrics(int gridTotal, int gridCorrect, int notGridTotal, int falseGrids)
{
    var total = gridTotal + notGridTotal;
    var notGridCorrect = notGridTotal - falseGrids;
    var correct = gridCorrect + notGridCorrect;
    return new(
    "model.pt", "prediction", total, correct, correct / (double)total, 0,
    new Dictionary<string, ModelClassMetrics>
    {
        ["grid"] = new(gridTotal, gridCorrect, gridCorrect / (double)gridTotal),
        ["not_grid"] = new(notGridTotal, notGridCorrect, notGridCorrect / (double)notGridTotal)
    },
    new Dictionary<string, Dictionary<string, int>>
    {
        ["grid"] = new() { ["grid"] = gridCorrect, ["not_grid"] = gridTotal - gridCorrect },
        ["not_grid"] = new() { ["grid"] = falseGrids, ["not_grid"] = notGridCorrect }
    });
}
