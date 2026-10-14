using System.Globalization;
using System.Text.RegularExpressions;

namespace Cove.DeleteImageGrids;

public readonly record struct TrainingEpoch(int Current, int Total, bool Completed);
public readonly record struct TrainingCheckpoint(int Epoch, int TotalEpochs, double Accuracy, double Loss);

public static partial class TrainingOutputParser
{
    public static bool TryParseEpoch(string line, out TrainingEpoch epoch)
    {
        epoch = default;
        var starting = StartingEpochRegex().Match(line);
        var completed = starting.Success ? null : CompletedEpochRegex().Match(line);
        var match = starting.Success ? starting : completed;
        if (match is null || !match.Success
            || !int.TryParse(match.Groups["current"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var current)
            || !int.TryParse(match.Groups["total"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var total))
            return false;
        epoch = new(current, total, completed is not null);
        return current > 0 && total > 0 && current <= total;
    }

    public static bool TryParseCheckpoint(string line, out TrainingCheckpoint checkpoint)
    {
        checkpoint = default;
        var match = CheckpointRegex().Match(line);
        if (!match.Success
            || !int.TryParse(match.Groups["epoch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            || !int.TryParse(match.Groups["total"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var total)
            || !TryParseNumber(match.Groups["accuracy"].Value, out var accuracy)
            || !TryParseNumber(match.Groups["loss"].Value, out var loss))
            return false;
        checkpoint = new(epoch, total, accuracy, loss);
        return epoch > 0 && total > 0 && epoch <= total;
    }

    public static bool TryParseBestValidationAccuracy(string line, out double accuracy)
    {
        accuracy = default;
        var match = LegacyBestAccuracyRegex().Match(line);
        return match.Success && TryParseNumber(match.Groups["accuracy"].Value, out accuracy);
    }

    public static bool TryParseEvaluationAccuracy(string line, out double accuracy)
    {
        accuracy = default;
        var match = EvaluationAccuracyRegex().Match(line);
        return match.Success && TryParseNumber(match.Groups["accuracy"].Value, out accuracy);
    }

    private static bool TryParseNumber(string value, out double number) =>
        double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number)
        && double.IsFinite(number);

    [GeneratedRegex(@"^Starting epoch\s+(?<current>\d+)\s*/\s*(?<total>\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex StartingEpochRegex();

    [GeneratedRegex(@"^Epoch\s+(?<current>\d+)\s*/\s*(?<total>\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompletedEpochRegex();

    [GeneratedRegex(@"^Best checkpoint:\s*epoch\s+(?<epoch>\d+)\s*/\s*(?<total>\d+)\s*,\s*val acc\s+(?<accuracy>\d+(?:\.\d+)?)\s*,\s*val loss\s+(?<loss>\d+(?:\.\d+)?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CheckpointRegex();

    [GeneratedRegex(@"Best val acc\s+(?<accuracy>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyBestAccuracyRegex();

    [GeneratedRegex(@"Overall accuracy:\s*(?<accuracy>\d+(?:\.\d+)?)%", RegexOptions.IgnoreCase)]
    private static partial Regex EvaluationAccuracyRegex();
}
