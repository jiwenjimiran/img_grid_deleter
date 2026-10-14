namespace Cove.DeleteImageGrids;

public static class TrainingDatasetDeduplication
{
    public static string? FindByDigest(string folder, string digest) =>
        Directory.EnumerateFiles(folder, $"*-{digest}.*", SearchOption.TopDirectoryOnly).FirstOrDefault();
}
