#if DELETE_IMAGE_GRIDS_DEV
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.DeleteImageGrids;

public static class TrainingRepository
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

    public static RepositoryStatus Inspect(string? path, string? activeModelPath = null)
    {
        var normalized = DeveloperSettings.Normalize(new DeveloperSettings(path ?? "")).TrainingRepoPath;
        if (normalized.Length == 0) return new(false, "", 0, 0, "Choose a model training repository.", activeModelPath);
        try
        {
            var required = new[]
            {
                Path.Combine(normalized, "train.py"), Path.Combine(normalized, "evaluate.py"),
                PythonPath(normalized), Path.Combine(normalized, "data", "grid"),
                Path.Combine(normalized, "data", "not_grid")
            };
            var missing = required.FirstOrDefault(candidate => !File.Exists(candidate) && !Directory.Exists(candidate));
            if (missing is not null)
                return new(false, normalized, 0, 0, $"Missing required training project item: {missing}", activeModelPath);
            return new(true, normalized, CountImages(Path.Combine(normalized, "data", "grid")),
                CountImages(Path.Combine(normalized, "data", "not_grid")), null, activeModelPath);
        }
        catch (Exception ex) { return new(false, normalized, 0, 0, ex.Message, activeModelPath); }
    }

    public static string PythonPath(string root)
    {
        var windows = Path.Combine(root, ".venv", "Scripts", "python.exe");
        return File.Exists(windows) ? windows : Path.Combine(root, ".venv", "bin", "python");
    }

    private static int CountImages(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Count(path => ImageExtensions.Contains(Path.GetExtension(path)));
}

public static class ExtensionBuildRepository
{
    public static BuildRepositoryStatus Inspect(string? path)
    {
        var normalized = DeveloperSettings.Normalize(new DeveloperSettings("", path ?? "")).ExtensionRepoPath;
        if (normalized.Length == 0) return new(false, "", "Choose the Delete Image Grids source repository.");
        var package = Path.Combine(normalized, "scripts", "package.ps1");
        var project = Path.Combine(normalized, "src", "DeleteImageGrids", "DeleteImageGrids.csproj");
        return File.Exists(package) && File.Exists(project)
            ? new(true, normalized, null)
            : new(false, normalized, "The selected folder is not a Delete Image Grids source repository.");
    }
}

public sealed class TrainingSampleCollector(IExtensionServiceScopeFactory scopeFactory, GridRunCoordinator runs)
{
    private const int DirectBatchSize = 100;

    public async Task<AddTrainingSamplesResponse> AddAsync(
        DeveloperSettings settings, AddTrainingSamplesRequest request, CancellationToken ct)
    {
        var repo = TrainingRepository.Inspect(settings.TrainingRepoPath);
        if (!repo.Valid) throw new InvalidOperationException(repo.Error);
        var label = request.Label?.Trim().ToLowerInvariant();
        if (label is not "grid" and not "not_grid") throw new InvalidOperationException("Label must be grid or not_grid.");
        var ids = (request.ImageIds ?? []).Where(id => id > 0).Distinct().ToArray();
        IReadOnlyList<GridMatch>? explicitMatches = null;
        if (request.Selection is null)
        {
            if (ids.Length == 0) throw new InvalidOperationException("Select at least one classified image.");
            explicitMatches = await runs.GetMatchesAsync(request.RunId, ids, ct);
            if (explicitMatches is null || explicitMatches.Count != ids.Length)
                throw new InvalidOperationException("Selections must belong to a saved classification run.");
        }
        else if (!string.Equals(request.Selection.RunId, request.RunId, StringComparison.Ordinal))
            throw new InvalidOperationException("The selection does not belong to the requested run.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var added = 0;
        var relabeled = 0;
        var skipped = 0;
        var errors = new List<string>();
        var batches = request.Selection is null
            ? SingleBatch(explicitMatches!)
            : runs.ReadSelectionBatchesAsync(request.Selection, 100, ct);
        await foreach (var matches in batches.WithCancellation(ct))
        {
            var labeledImageIds = new List<int>();
            var fileIds = matches.Select(match => match.InScopeFiles.First().Id).ToArray();
            var candidates = await db.ImageFiles.AsNoTracking()
                .Where(file => fileIds.Contains(file.Id) && file.ImageId != null)
                .Select(file => new ImageFileCandidate(file.Id, file.ImageId!.Value, file.Basename, file.Path,
                    file.Size, file.Width, file.Height, file.ZipFileId))
                .ToListAsync(ct);
            var byId = candidates.ToDictionary(candidate => candidate.ImageId);
            foreach (var match in matches)
            {
                ct.ThrowIfCancellationRequested();
                if (!byId.TryGetValue(match.ImageId, out var candidate))
                {
                    errors.Add($"Image {match.ImageId}: source file is no longer available.");
                    continue;
                }
                try
                {
                    await using var source = await CoveImageSource.OpenAsync(db, candidate, ct)
                        ?? throw new FileNotFoundException("Source image is unavailable.", candidate.Path);
                    using var hash = SHA256.Create();
                    var digest = Convert.ToHexString(await hash.ComputeHashAsync(source, ct)).ToLowerInvariant();
                    source.Position = 0;
                    var extension = Path.GetExtension(candidate.Basename).ToLowerInvariant();
                    if (extension is not ".jpg" and not ".jpeg" and not ".png" and not ".bmp" and not ".gif" and not ".webp")
                        throw new InvalidDataException($"Unsupported training image format: {extension}");
                    var destinationFolder = Path.Combine(repo.Path, "data", label);
                    var oppositeFolder = Path.Combine(repo.Path, "data", label == "grid" ? "not_grid" : "grid");
                    var existingTarget = TrainingDatasetDeduplication.FindByDigest(destinationFolder, digest);
                    if (existingTarget is not null) { skipped++; labeledImageIds.Add(match.ImageId); continue; }
                    var destination = Path.Combine(destinationFolder, $"cove-{match.ImageId}-{digest}{extension}");
                    var opposite = TrainingDatasetDeduplication.FindByDigest(oppositeFolder, digest);
                    if (opposite is not null) { File.Move(opposite, destination); relabeled++; }
                    else
                    {
                        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                        await source.CopyToAsync(output, ct);
                        added++;
                    }
                    labeledImageIds.Add(match.ImageId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{Path.GetFileName(match.Path)}: {ex.Message}");
                }
            }
            await GridMetadataPublisher.ApplyTrainingLabelAsync(db, labeledImageIds, label == "grid", ct);
            db.ChangeTracker.Clear();
        }
        return new(added, relabeled, skipped, errors);
    }

    public async Task<AddTrainingSamplesResponse> AddImagesAsync(
        DeveloperSettings settings, AddImageTrainingSamplesRequest request, CancellationToken ct)
    {
        var repo = TrainingRepository.Inspect(settings.TrainingRepoPath);
        if (!repo.Valid) throw new InvalidOperationException(repo.Error);
        var label = request.Label?.Trim().ToLowerInvariant();
        if (label is not "grid" and not "not_grid") throw new InvalidOperationException("Label must be grid or not_grid.");
        var ids = (request.ImageIds ?? []).Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) throw new InvalidOperationException("Select at least one image.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var added = 0;
        var relabeled = 0;
        var skipped = 0;
        var errors = new List<string>();
        for (var offset = 0; offset < ids.Length; offset += DirectBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var labeledImageIds = new List<int>();
            var batchIds = ids.Skip(offset).Take(DirectBatchSize).ToArray();
            var files = await db.ImageFiles.AsNoTracking()
                .Where(file => file.ImageId != null && batchIds.Contains(file.ImageId.Value))
                .OrderBy(file => file.ImageId).ThenBy(file => file.Id)
                .Select(file => new ImageFileCandidate(file.Id, file.ImageId!.Value, file.Basename, file.Path,
                    file.Size, file.Width, file.Height, file.ZipFileId))
                .ToListAsync(ct);
            var byImage = files.GroupBy(file => file.ImageId).ToDictionary(group => group.Key, group => group.ToList());
            foreach (var imageId in batchIds)
            {
                ct.ThrowIfCancellationRequested();
                if (!byImage.TryGetValue(imageId, out var candidates) || candidates.Count == 0)
                {
                    errors.Add($"Image {imageId}: no source files are available.");
                    continue;
                }
                Exception? lastError = null;
                var stored = false;
                foreach (var candidate in candidates)
                {
                    try
                    {
                        var outcome = await StoreDirectSampleAsync(db, repo.Path, label, imageId, candidate, ct);
                        if (outcome == SampleOutcome.Added) added++;
                        else if (outcome == SampleOutcome.Relabeled) relabeled++;
                        else skipped++;
                        stored = true;
                        labeledImageIds.Add(imageId);
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { lastError = ex; }
                }
                if (!stored)
                    errors.Add($"Image {imageId}: {lastError?.Message ?? "source image is unavailable."}");
            }
            await GridMetadataPublisher.ApplyTrainingLabelAsync(db, labeledImageIds, label == "grid", ct);
            db.ChangeTracker.Clear();
        }
        return new(added, relabeled, skipped, errors);
    }

    private static async Task<SampleOutcome> StoreDirectSampleAsync(
        CoveContext db, string repository, string label, int imageId, ImageFileCandidate candidate, CancellationToken ct)
    {
        await using var source = await CoveImageSource.OpenAsync(db, candidate, ct)
            ?? throw new FileNotFoundException("Source image is unavailable.", candidate.Path);
        var extension = Path.GetExtension(candidate.Basename).ToLowerInvariant();
        if (extension is not ".jpg" and not ".jpeg" and not ".png" and not ".bmp" and not ".gif" and not ".webp")
            throw new InvalidDataException($"Unsupported training image format: {extension}");
        using var hash = SHA256.Create();
        var digest = Convert.ToHexString(await hash.ComputeHashAsync(source, ct)).ToLowerInvariant();
        if (!source.CanSeek) throw new InvalidDataException("The training image stream cannot be rewound.");
        source.Position = 0;
        var destinationFolder = Path.Combine(repository, "data", label);
        var oppositeFolder = Path.Combine(repository, "data", label == "grid" ? "not_grid" : "grid");
        if (TrainingDatasetDeduplication.FindByDigest(destinationFolder, digest) is not null)
            return SampleOutcome.Skipped;
        var destination = Path.Combine(destinationFolder, $"cove-{imageId}-{digest}{extension}");
        var opposite = TrainingDatasetDeduplication.FindByDigest(oppositeFolder, digest);
        if (opposite is not null)
        {
            File.Move(opposite, destination);
            return SampleOutcome.Relabeled;
        }
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await source.CopyToAsync(output, ct);
        return SampleOutcome.Added;
    }

    private enum SampleOutcome { Added, Relabeled, Skipped }

    private static async IAsyncEnumerable<List<GridMatch>> SingleBatch(IReadOnlyList<GridMatch> matches)
    {
        yield return matches.ToList();
        await Task.CompletedTask;
    }
}

public sealed class ModelTrainingCoordinator(ImageGridClassifier classifier, string extensionRoot)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly object _gate = new();
    private MutableTrainingStatus _current = new();
    private Process? _process;
    private Process? _updateHelper;
    private string? _statusPath;
    private DateTimeOffset _lastPersistedAt = DateTimeOffset.MinValue;

    public bool IsActive { get { lock (_gate) return _current.Status is "queued" or "running"; } }

    public bool TryStart(DeveloperSettings settings, IJobService jobs, out StartTrainingResponse? response)
    {
        lock (_gate)
        {
            if (_current.Status is "queued" or "running") { response = null; return false; }
            var id = Guid.NewGuid().ToString("N");
            SetStatusPath(settings);
            _current = new MutableTrainingStatus { TrainingId = id, Status = "queued", Stage = "Queued", Progress = 0 };
            var jobId = jobs.Enqueue("ext:image-grid-training", "[Delete Image Grids] Retrain classifier",
                (progress, ct) => RunAsync(id, settings, progress, ct), exclusive: true);
            _current.JobId = jobId;
            PersistLocked(force: true);
            response = new(id, jobId);
            return true;
        }
    }

    public TrainingStatus GetStatus()
    {
        lock (_gate) return _current.Snapshot(classifier.ActiveModelPath, classifier.ActiveModelVersion);
    }

    public void TryLoadRepositoryModel(DeveloperSettings settings)
    {
        var repo = TrainingRepository.Inspect(settings.TrainingRepoPath);
        if (!repo.Valid) return;
        var model = Path.Combine(repo.Path, "image-grid-classifier.onnx");
        var versionPath = Path.Combine(repo.Path, "image-grid-classifier.version");
        var version = File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : "local model";
        if (File.Exists(model)) classifier.Reload(model, version);
    }

    public void TryRestoreStatus(DeveloperSettings settings)
    {
        lock (_gate)
        {
            SetStatusPath(settings);
            if (_statusPath is null || !File.Exists(_statusPath)) return;
            try
            {
                var restored = JsonSerializer.Deserialize<MutableTrainingStatus>(File.ReadAllText(_statusPath), JsonOptions);
                if (restored is null) return;
                restored.Output ??= [];
                if (restored.Output.Count > 300) restored.Output = restored.Output.TakeLast(300).ToList();
                if (restored.Status is "queued" or "running")
                {
                    restored.Status = "interrupted";
                    restored.Stage = "Interrupted by Cove restart";
                    restored.Error = "The previous training process ended when Cove stopped. Start a new training run to continue.";
                    restored.JobId = null;
                }
                _current = restored;
                PersistLocked(force: true);
            }
            catch { /* A malformed report must not prevent the extension from loading. */ }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
    }

    private async Task RunAsync(string id, DeveloperSettings settings, Cove.Core.Interfaces.IJobProgress progress, CancellationToken ct)
    {
        var repo = TrainingRepository.Inspect(settings.TrainingRepoPath, classifier.ActiveModelPath);
        if (!repo.Valid) throw new InvalidOperationException(repo.Error);
        var buildRepo = ExtensionBuildRepository.Inspect(settings.ExtensionRepoPath);
        if (!buildRepo.Valid) throw new InvalidOperationException(buildRepo.Error);
        var prediction = Path.Combine(repo.Path, "prediction");
        if (!Directory.Exists(Path.Combine(prediction, "grid")) || !Directory.Exists(Path.Combine(prediction, "not_grid")))
            throw new InvalidOperationException("The grid and not_grid prediction folders are required for guarded model promotion.");
        var activePt = Path.Combine(repo.Path, "model.pt");
        if (!File.Exists(activePt)) throw new FileNotFoundException("The active training checkpoint is missing.", activePt);

        var candidateVersion = ModelVersionRegistry.Reserve(buildRepo.Path, repo.Path);
        var archiveDirectory = Path.Combine(repo.Path, "models", candidateVersion);
        var candidatePt = Path.Combine(archiveDirectory, "model.pt");
        var candidateOnnx = Path.Combine(archiveDirectory, "image-grid-classifier.onnx");
        var candidatePreprocess = Path.ChangeExtension(candidateOnnx, ".preprocess");
        var baselineMetricsPath = Path.Combine(archiveDirectory, "baseline-evaluation.json");
        var candidateMetricsPath = Path.Combine(archiveDirectory, "evaluation.json");
        ModelEvaluationMetrics? baselineMetrics = null;
        ModelEvaluationMetrics? candidateMetrics = null;
        var seedResults = new List<SeedRunResult>();
        SeedRunResult? winningSeed = null;
        var baselineVersion = classifier.ActiveModelVersion;
        Set(status: "running", stage: "Training", error: null, progressValue: 0,
            baselineVersion: baselineVersion, candidateVersion: candidateVersion);
        try
        {
            var seeds = new[] { 42, 1337, 2026 };
            var splitManifest = Path.Combine(archiveDirectory, "split-manifest.json");
            for (var seedIndex = 0; seedIndex < seeds.Length; seedIndex++)
            {
                var seed = seeds[seedIndex];
                var seedDirectory = Path.Combine(archiveDirectory, "seeds", seed.ToString());
                Directory.CreateDirectory(seedDirectory);
                var seedCheckpoint = Path.Combine(seedDirectory, "model.pt");
                var seedSummaryPath = Path.Combine(seedDirectory, "training-summary.json");
                var seedEvaluationPath = Path.Combine(seedDirectory, "evaluation.json");
                Set(stage: $"Training seed {seedIndex + 1} of {seeds.Length} ({seed})",
                    progressValue: seedIndex / (double)seeds.Length * .78);
                await RunProcessAsync(repo.Path, TrainingRepository.PythonPath(repo.Path),
                ["train.py", "--data-dir", "data", "--out", seedCheckpoint, "--arch", "resnet18",
                 "--epochs", "10", "--batch-size", "32", "--img-size", "384", "--lr", "0.001",
                 "--patience", "3", "--finetune", "--seed", seed.ToString(), "--split-seed", "42",
                 "--split-manifest", splitManifest, "--summary-output", seedSummaryPath], progress, ct, line =>
                {
                    if (TrainingOutputParser.TryParseEpoch(line, out var epoch))
                    {
                        Set(stage: $"Training seed {seedIndex + 1} of {seeds.Length} ({seed}) — epoch {epoch.Current} of {epoch.Total}");
                        var completed = epoch.Completed ? epoch.Current : epoch.Current - 1;
                        var fraction = (seedIndex + completed / (double)epoch.Total) / seeds.Length;
                        var trainingProgress = Math.Clamp(fraction * .78, 0d, .78d);
                        Set(progressValue: trainingProgress);
                        progress.Report(trainingProgress, line);
                    }
                    if (TrainingOutputParser.TryParseCheckpoint(line, out var checkpoint))
                        Set(bestValidation: checkpoint.Accuracy, bestValidationLoss: checkpoint.Loss,
                            bestEpoch: checkpoint.Epoch);
                    else if (TrainingOutputParser.TryParseBestValidationAccuracy(line, out var legacyAccuracy))
                        Set(bestValidation: legacyAccuracy);
                });

                var evaluationProgress = .78 + seedIndex / (double)seeds.Length * .09;
                Set(stage: $"Evaluating seed {seedIndex + 1} of {seeds.Length} ({seed}) on all prediction images",
                    progressValue: evaluationProgress);
                progress.Report(evaluationProgress,
                    $"Evaluating seed {seed} on the full prediction set");
                await RunProcessAsync(repo.Path, TrainingRepository.PythonPath(repo.Path),
                    ["evaluate.py", "prediction", "--model", seedCheckpoint, "--no-log", "--quiet",
                     "--json-output", seedEvaluationPath], progress, ct, null);
                var summary = JsonSerializer.Deserialize<SeedTrainingSummary>(
                    await File.ReadAllTextAsync(seedSummaryPath, ct), JsonOptions)
                    ?? throw new InvalidDataException($"Training summary was invalid for seed {seed}.");
                seedResults.Add(new(seed, seedCheckpoint, seedEvaluationPath, summary,
                    ModelQualityGate.ReadMetrics(seedEvaluationPath)));
            }

            winningSeed = seedResults.Aggregate((best, next) => IsBetterSeed(next, best) ? next : best);
            File.Copy(winningSeed.Checkpoint, candidatePt, true);
            File.Copy(winningSeed.EvaluationPath, candidateMetricsPath, true);
            candidateMetrics = winningSeed.Metrics;
            Set(evaluation: candidateMetrics.Accuracy, winningSeed: winningSeed.Seed,
                falseGrids: candidateMetrics.Confusion["not_grid"]["grid"],
                gridCorrect: candidateMetrics.PerClass["grid"].Correct,
                candidateMetrics: Summarize(candidateMetrics));

            Set(stage: "Exporting ONNX", progressValue: .88);
            progress.Report(.88, $"Exporting and verifying seed {winningSeed.Seed} for candidate {candidateVersion}");
            var exporter = Path.Combine(extensionRoot, "dev-tools", "export-model.py");
            await RunProcessAsync(repo.Path, TrainingRepository.PythonPath(repo.Path),
                [exporter, candidatePt, candidateOnnx, "--verify-dir", Path.Combine(repo.Path, "data"), "--verify-limit", "64"],
                progress, ct, null);

            Set(stage: $"Evaluating active model {baselineVersion}", progressValue: .91);
            progress.Report(.91, $"Evaluating active model {baselineVersion}");
            await RunProcessAsync(repo.Path, TrainingRepository.PythonPath(repo.Path),
                ["evaluate.py", "prediction", "--model", activePt, "--no-log", "--quiet", "--json-output", baselineMetricsPath],
                progress, ct, null);
            baselineMetrics = ModelQualityGate.ReadMetrics(baselineMetricsPath);

            Set(evaluation: candidateMetrics.Accuracy, baselineMetrics: Summarize(baselineMetrics));
            var quality = ModelQualityGate.Compare(baselineMetrics, candidateMetrics);
            Set(qualityGateMessage: quality.Message);
            if (!quality.Accepted)
            {
                ModelVersionRegistry.WriteMetadata(archiveDirectory, new
                {
                    version = candidateVersion,
                    status = "rejected",
                    activeVersion = classifier.ActiveModelVersion,
                    reason = quality.Message,
                    winningSeed = winningSeed.Seed,
                    seeds = seedResults,
                    baseline = baselineMetrics,
                    candidate = candidateMetrics,
                    completedAt = DateTimeOffset.Now
                });
                throw new ModelQualityRejectedException($"{quality.Message} Active model {classifier.ActiveModelVersion} was kept.");
            }

            Set(stage: "Building extension packages", progressValue: .96);
            progress.Report(.96, "Building standard and developer extension packages");
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            await RunProcessAsync(buildRepo.Path, powershell,
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(buildRepo.Path, "scripts", "package.ps1"),
                 "-Configuration", "Release", "-TrainingRepo", repo.Path, "-Checkpoint", candidatePt, "-Version", candidateVersion], progress, ct, null);
            var receiptPath = Path.Combine(buildRepo.Path, "artifacts", "latest-build.json");
            var receipt = JsonSerializer.Deserialize<BuildReceipt>(await File.ReadAllTextAsync(receiptPath, ct),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("The extension build did not produce a build receipt.");
            if (!string.Equals(receipt.Version, candidateVersion, StringComparison.Ordinal))
                throw new InvalidDataException($"Expected build {candidateVersion}, but packaging produced {receipt.Version}.");
            Set(stage: "Activating model", progressValue: .99);
            Promote(repo.Path, candidatePt, candidateOnnx, candidatePreprocess, receipt.Version);
            classifier.Reload(Path.Combine(repo.Path, "image-grid-classifier.onnx"), receipt.Version);
            StageUpdate(buildRepo.Path, receipt);
            ModelVersionRegistry.WriteMetadata(archiveDirectory, new
            {
                version = candidateVersion,
                status = "accepted",
                previousActiveVersion = File.Exists(Path.Combine(repo.Path, "image-grid-classifier.previous.version"))
                    ? File.ReadAllText(Path.Combine(repo.Path, "image-grid-classifier.previous.version")).Trim() : null,
                qualityGate = quality.Message,
                winningSeed = winningSeed.Seed,
                seeds = seedResults,
                baseline = baselineMetrics,
                candidate = candidateMetrics,
                completedAt = DateTimeOffset.Now
            });
            Set(build: receipt);
            progress.Report(1, $"Version {receipt.Version} built and staged. Restart Cove to load it.");
            Set(status: "completed", stage: "Complete", progressValue: 1);
        }
        catch (OperationCanceledException)
        {
            ModelVersionRegistry.WriteMetadata(archiveDirectory, new { version = candidateVersion, status = "cancelled", completedAt = DateTimeOffset.Now });
            Set(status: "cancelled", stage: "Cancelled");
            throw;
        }
        catch (ModelQualityRejectedException ex)
        {
            Set(status: "failed", stage: "Quality gate failed", error: ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            ModelVersionRegistry.WriteMetadata(archiveDirectory, new
            {
                version = candidateVersion,
                status = "failed",
                error = ex.Message,
                winningSeed = winningSeed?.Seed,
                seeds = seedResults,
                baseline = baselineMetrics,
                candidate = candidateMetrics,
                completedAt = DateTimeOffset.Now
            });
            Set(status: "failed", stage: "Failed", error: ex.Message);
            throw;
        }
        finally
        {
            lock (_gate) _process = null;
        }
    }

    private async Task RunProcessAsync(string workingDirectory, string executable, IReadOnlyList<string> arguments,
        Cove.Core.Interfaces.IJobProgress progress, CancellationToken ct, Action<string>? inspect)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONUNBUFFERED"] = "1";
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Could not start the training process.");
        lock (_gate) _process = process;
        try
        {
            using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            var stdout = PumpAsync(process.StandardOutput, inspect, ct);
            var stderr = PumpAsync(process.StandardError, inspect, ct);
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0) throw new InvalidOperationException($"Training command exited with code {process.ExitCode}. See output for details.");
        }
        finally
        {
            lock (_gate) if (ReferenceEquals(_process, process)) _process = null;
        }
    }

    private async Task PumpAsync(StreamReader reader, Action<string>? inspect, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            inspect?.Invoke(line);
            lock (_gate)
            {
                _current.Output.Add(line);
                if (_current.Output.Count > 300) _current.Output.RemoveAt(0);
                PersistLocked(force: false);
            }
        }
    }

    private static void Promote(string repo, string candidatePt, string candidateOnnx, string candidatePreprocess, string version)
    {
        var modelPt = Path.Combine(repo, "model.pt");
        var modelOnnx = Path.Combine(repo, "image-grid-classifier.onnx");
        var backupPt = Path.Combine(repo, "model.previous.pt");
        var backupOnnx = Path.Combine(repo, "image-grid-classifier.previous.onnx");
        var preprocess = Path.Combine(repo, "image-grid-classifier.preprocess");
        var backupPreprocess = Path.Combine(repo, "image-grid-classifier.previous.preprocess");
        var versionPath = Path.Combine(repo, "image-grid-classifier.version");
        var backupVersion = Path.Combine(repo, "image-grid-classifier.previous.version");
        if (File.Exists(modelPt)) File.Copy(modelPt, backupPt, true);
        if (File.Exists(modelOnnx)) File.Copy(modelOnnx, backupOnnx, true);
        if (File.Exists(preprocess)) File.Copy(preprocess, backupPreprocess, true);
        if (File.Exists(versionPath)) File.Copy(versionPath, backupVersion, true);
        try
        {
            File.Copy(candidatePt, modelPt, true);
            File.Copy(candidateOnnx, modelOnnx, true);
            File.Copy(candidatePreprocess, preprocess, true);
            File.WriteAllText(versionPath, version);
        }
        catch
        {
            if (File.Exists(backupPt)) File.Copy(backupPt, modelPt, true);
            if (File.Exists(backupOnnx)) File.Copy(backupOnnx, modelOnnx, true);
            if (File.Exists(backupPreprocess)) File.Copy(backupPreprocess, preprocess, true);
            if (File.Exists(backupVersion)) File.Copy(backupVersion, versionPath, true);
            throw;
        }
    }

    private void StageUpdate(string buildRepo, BuildReceipt receipt)
    {
        lock (_gate)
        {
            try
            {
                if (_updateHelper is { HasExited: false }) return;
                _updateHelper?.Dispose();
                _updateHelper = null;
            }
            catch { _updateHelper = null; }
        }
        var helper = Path.Combine(extensionRoot, "dev-tools", "apply-update.ps1");
        var source = Path.GetFullPath(receipt.DeveloperPublishDirectory);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Developer publish directory is missing: {source}");
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = buildRepo };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", helper,
                     "-Source", source, "-Destination", extensionRoot, "-CoveProcessId", Environment.ProcessId.ToString(),
                     "-Receipt", Path.Combine(buildRepo, "artifacts", "update-applied.json") })
            start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the staged update helper.");
        lock (_gate) _updateHelper = process;
    }

    private void Set(string? status = null, string? stage = null, string? error = null,
        double? bestValidation = null, double? bestValidationLoss = null, int? bestEpoch = null,
        double? evaluation = null, string? candidateVersion = null, string? qualityGateMessage = null,
        BuildReceipt? build = null, int? winningSeed = null, int? falseGrids = null, int? gridCorrect = null,
        double? progressValue = null, string? baselineVersion = null,
        TrainingEvaluationSummary? baselineMetrics = null, TrainingEvaluationSummary? candidateMetrics = null)
    {
        lock (_gate)
        {
            if (status is not null) _current.Status = status;
            if (stage is not null) _current.Stage = stage;
            if (error is not null || status == "running") _current.Error = error;
            if (bestValidation.HasValue) _current.BestValidationAccuracy = bestValidation;
            if (bestValidationLoss.HasValue) _current.BestValidationLoss = bestValidationLoss;
            if (bestEpoch.HasValue) _current.BestEpoch = bestEpoch;
            if (evaluation.HasValue) _current.EvaluationAccuracy = evaluation;
            if (winningSeed.HasValue) _current.WinningSeed = winningSeed;
            if (falseGrids.HasValue) _current.EvaluationFalseGrids = falseGrids;
            if (gridCorrect.HasValue) _current.EvaluationGridCorrect = gridCorrect;
            if (progressValue.HasValue) _current.Progress = Math.Clamp(progressValue.Value, 0, 1);
            if (baselineVersion is not null) _current.BaselineVersion = baselineVersion;
            if (baselineMetrics is not null) _current.BaselineMetrics = baselineMetrics;
            if (candidateMetrics is not null) _current.CandidateMetrics = candidateMetrics;
            if (candidateVersion is not null) _current.CandidateVersion = candidateVersion;
            if (qualityGateMessage is not null) _current.QualityGateMessage = qualityGateMessage;
            if (build is not null)
            {
                _current.BuiltVersion = build.Version;
                _current.NormalArtifact = build.NormalArtifact;
                _current.DeveloperArtifact = build.DeveloperArtifact;
                _current.UpdateStaged = true;
            }
            PersistLocked(force: true);
        }
    }

    private static TrainingEvaluationSummary Summarize(ModelEvaluationMetrics metrics)
    {
        var grid = metrics.PerClass["grid"];
        var notGrid = metrics.PerClass["not_grid"];
        var falseGrids = metrics.Confusion["not_grid"]["grid"];
        return new(metrics.Total, metrics.Correct, metrics.Accuracy, metrics.Skipped,
            grid.Total, grid.Correct, grid.Total == 0 ? 0 : grid.Correct / (double)grid.Total,
            notGrid.Total, falseGrids, notGrid.Total == 0 ? 0 : falseGrids / (double)notGrid.Total);
    }

    private void SetStatusPath(DeveloperSettings settings)
    {
        var repository = TrainingRepository.Inspect(settings.TrainingRepoPath);
        _statusPath = repository.Valid
            ? Path.Combine(repository.Path, ".delete-image-grids", "latest-training-status.json")
            : null;
    }

    private void PersistLocked(bool force)
    {
        if (_statusPath is null) return;
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastPersistedAt < TimeSpan.FromSeconds(1)) return;
        try
        {
            var directory = Path.GetDirectoryName(_statusPath)!;
            Directory.CreateDirectory(directory);
            var temporary = _statusPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_current, JsonOptions));
            File.Move(temporary, _statusPath, true);
            _lastPersistedAt = now;
        }
        catch { /* Reporting failures must not fail training. */ }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private sealed class MutableTrainingStatus
    {
        public string? TrainingId { get; set; }
        public string Status { get; set; } = "idle";
        public string? JobId { get; set; }
        public string? Stage { get; set; }
        public string? Error { get; set; }
        public double Progress { get; set; }
        public double? BestValidationAccuracy { get; set; }
        public double? BestValidationLoss { get; set; }
        public int? BestEpoch { get; set; }
        public double? EvaluationAccuracy { get; set; }
        public int? WinningSeed { get; set; }
        public int? EvaluationFalseGrids { get; set; }
        public int? EvaluationGridCorrect { get; set; }
        public string? CandidateVersion { get; set; }
        public string? BaselineVersion { get; set; }
        public TrainingEvaluationSummary? BaselineMetrics { get; set; }
        public TrainingEvaluationSummary? CandidateMetrics { get; set; }
        public string? QualityGateMessage { get; set; }
        public List<string> Output { get; set; } = [];
        public string? BuiltVersion { get; set; }
        public string? NormalArtifact { get; set; }
        public string? DeveloperArtifact { get; set; }
        public bool UpdateStaged { get; set; }
        public TrainingStatus Snapshot(string activeModelPath, string activeModelVersion) => new(TrainingId, Status, JobId, Stage, Error, Progress,
            BestValidationAccuracy, BestValidationLoss, BestEpoch, EvaluationAccuracy, WinningSeed,
            EvaluationFalseGrids, EvaluationGridCorrect, BaselineVersion, CandidateVersion,
            BaselineMetrics, CandidateMetrics,
            QualityGateMessage, [.. Output], activeModelPath,
            activeModelVersion, BuiltVersion,
            NormalArtifact, DeveloperArtifact, UpdateStaged);
    }

    private sealed record BuildReceipt(string Version, string NormalArtifact, string DeveloperArtifact,
        string DeveloperPublishDirectory);

    private sealed record SeedTrainingSummary(int Seed, int SplitSeed, int Epoch,
        double ValidationAccuracy, double ValidationLoss, int FalseGrids,
        int GridCorrect, int GridTotal, string Checkpoint);

    private sealed record SeedRunResult(int Seed, string Checkpoint, string EvaluationPath,
        SeedTrainingSummary Training, ModelEvaluationMetrics Metrics);

    private static bool IsBetterSeed(SeedRunResult candidate, SeedRunResult baseline)
    {
        var candidateFalseGrids = candidate.Metrics.Confusion["not_grid"]["grid"];
        var baselineFalseGrids = baseline.Metrics.Confusion["not_grid"]["grid"];
        if (candidateFalseGrids != baselineFalseGrids) return candidateFalseGrids < baselineFalseGrids;
        var candidateGridCorrect = candidate.Metrics.PerClass["grid"].Correct;
        var baselineGridCorrect = baseline.Metrics.PerClass["grid"].Correct;
        if (candidateGridCorrect != baselineGridCorrect) return candidateGridCorrect > baselineGridCorrect;
        if (candidate.Training.ValidationLoss != baseline.Training.ValidationLoss)
            return candidate.Training.ValidationLoss < baseline.Training.ValidationLoss;
        return candidate.Seed < baseline.Seed;
    }

    private sealed class ModelQualityRejectedException(string message) : InvalidOperationException(message);
}
#endif
