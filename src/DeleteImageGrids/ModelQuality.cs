using System.Text.Json;

namespace Cove.DeleteImageGrids;

public sealed record ModelClassMetrics(int Total, int Correct, double Accuracy);
public sealed record ModelEvaluationMetrics(
    string Model,
    string DataDir,
    int Total,
    int Correct,
    double Accuracy,
    int Skipped,
    Dictionary<string, ModelClassMetrics> PerClass,
    Dictionary<string, Dictionary<string, int>> Confusion);

public sealed record ModelQualityDecision(bool Accepted, string Message);

public static class ModelQualityGate
{
    public static ModelQualityDecision Compare(ModelEvaluationMetrics baseline, ModelEvaluationMetrics candidate)
    {
        if (baseline.Total <= 0 || candidate.Total <= 0 || baseline.Total != candidate.Total)
            return new(false, "Model training failed: baseline and candidate were not evaluated against the same prediction set.");
        if (!baseline.PerClass.TryGetValue("grid", out var baselineGrid)
            || !candidate.PerClass.TryGetValue("grid", out var candidateGrid)
            || baselineGrid.Total <= 0 || baselineGrid.Total != candidateGrid.Total)
            return new(false, "Model training failed: comparable grid-class metrics were unavailable.");

        if (!TryGetFalseGridCount(baseline, out var baselineFalseGrids)
            || !TryGetFalseGridCount(candidate, out var candidateFalseGrids))
            return new(false, "Model training failed: comparable false-grid metrics were unavailable.");

        if (candidateFalseGrids > baselineFalseGrids)
            return new(false, $"Model training failed: the candidate marked {candidateFalseGrids} non-grid images as grids; the active model marked {baselineFalseGrids}. The active model was kept.");

        if (candidateGrid.Correct < baselineGrid.Correct)
            return new(false, $"Model training failed: false grids did not increase ({candidateFalseGrids} vs {baselineFalseGrids}), but grid recall fell to {Percent(candidateGrid.Accuracy)} from {Percent(baselineGrid.Accuracy)}. The active model was kept.");

        if (candidateFalseGrids == baselineFalseGrids && candidateGrid.Correct == baselineGrid.Correct)
            return new(false, $"Model training did not improve: false grids remained {candidateFalseGrids} and grid recall remained {candidateGrid.Correct}/{candidateGrid.Total}. The active model was kept.");

        return new(true, $"Candidate passed: false grids {candidateFalseGrids} vs {baselineFalseGrids}; grid recall {Percent(candidateGrid.Accuracy)} vs {Percent(baselineGrid.Accuracy)}; overall {Percent(candidate.Accuracy)} vs {Percent(baseline.Accuracy)}.");
    }

    public static ModelEvaluationMetrics ReadMetrics(string path) =>
        JsonSerializer.Deserialize<ModelEvaluationMetrics>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException($"Evaluation metrics were invalid: {path}");

    private static string Percent(double value) => $"{value * 100:0.0}%";

    private static bool TryGetFalseGridCount(ModelEvaluationMetrics metrics, out int count)
    {
        count = 0;
        return metrics.Confusion.TryGetValue("not_grid", out var predictions)
            && predictions.TryGetValue("grid", out count)
            && count >= 0;
    }
}

public static class ModelVersionRegistry
{
    public static string NextVersion(string extensionRepo, string trainingRepo)
    {
        var highest = 0;
        var sequencePath = Path.Combine(extensionRepo, "artifacts", "build-sequence.txt");
        if (File.Exists(sequencePath) && int.TryParse(File.ReadAllText(sequencePath).Trim(), out var sequence))
            highest = Math.Max(highest, sequence);
        var models = Path.Combine(trainingRepo, "models");
        if (Directory.Exists(models))
        {
            foreach (var directory in Directory.EnumerateDirectories(models))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith("0.2.", StringComparison.Ordinal)
                    && int.TryParse(name[4..], out var modelSequence))
                    highest = Math.Max(highest, modelSequence);
            }
        }
        return $"0.2.{highest + 1}";
    }

    public static string Reserve(string extensionRepo, string trainingRepo)
    {
        var version = NextVersion(extensionRepo, trainingRepo);
        var directory = Path.Combine(trainingRepo, "models", version);
        Directory.CreateDirectory(directory);
        WriteMetadata(directory, new { version, status = "training", startedAt = DateTimeOffset.Now });
        return version;
    }

    public static void WriteMetadata(string directory, object metadata)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "metadata.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
