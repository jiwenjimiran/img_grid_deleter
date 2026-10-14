#if DELETE_IMAGE_GRIDS_DEV
namespace Cove.DeleteImageGrids;

public sealed record DeveloperSettings(string TrainingRepoPath = "", string ExtensionRepoPath = "")
{
    public static DeveloperSettings DiscoverDefaults()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var training = Path.Combine(documents, "gitea", "img_grid_classifier");
        var extension = Path.Combine(documents, "cove-dev", "img_grid_deleter");
        return new DeveloperSettings(Directory.Exists(training) ? training : "", Directory.Exists(extension) ? extension : "");
    }

    public static DeveloperSettings Normalize(DeveloperSettings? value)
    {
        return new DeveloperSettings(NormalizePath(value?.TrainingRepoPath), NormalizePath(value?.ExtensionRepoPath));
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path.Trim()); } catch { return ""; }
    }
}

public sealed record AddTrainingSamplesRequest(string RunId, List<int>? ImageIds, string Label, GridSelectionRequest? Selection = null);
public sealed record AddImageTrainingSamplesRequest(List<int>? ImageIds, string Label);
public sealed record AddTrainingSamplesResponse(int Added, int Relabeled, int Skipped, List<string> Errors);
public sealed record StartTrainingResponse(string TrainingId, string JobId);
public sealed record TrainingEvaluationSummary(
    int Total, int Correct, double Accuracy, int Skipped,
    int GridTotal, int GridCorrect, double GridRecall,
    int NotGridTotal, int FalseGrids, double FalseGridRate);
public sealed record TrainingStatus(
    string? TrainingId, string Status, string? JobId, string? Stage, string? Error,
    double Progress,
    double? BestValidationAccuracy, double? BestValidationLoss, int? BestEpoch,
    double? EvaluationAccuracy, int? WinningSeed, int? EvaluationFalseGrids, int? EvaluationGridCorrect,
    string? BaselineVersion, string? CandidateVersion,
    TrainingEvaluationSummary? BaselineMetrics, TrainingEvaluationSummary? CandidateMetrics,
    string? QualityGateMessage, List<string> Output,
    string? ActiveModelPath, string? ActiveModelVersion, string? BuiltVersion, string? NormalArtifact,
    string? DeveloperArtifact, bool UpdateStaged);
public sealed record RepositoryStatus(
    bool Valid, string Path, int GridCount, int NotGridCount, string? Error,
    string? ActiveModelPath);
public sealed record BuildRepositoryStatus(bool Valid, string Path, string? Error);
#endif
