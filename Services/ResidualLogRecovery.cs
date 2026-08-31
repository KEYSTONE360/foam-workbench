using System.IO;

namespace FoamWorkbench.Services;

public sealed record ResidualLogData(string FilePath, IReadOnlyList<ResidualSample> Samples);

public static class ResidualLogRecovery
{
    public static ResidualLogData? LoadLatest(string casePath)
    {
        foreach (var filePath in EnumerateCandidates(casePath))
        {
            try
            {
                var data = Load(filePath);
                if (data.Samples.Count > 0) return data;
            }
            catch (IOException)
            {
                // A log may still be locked by another process; continue with older logs.
            }
            catch (UnauthorizedAccessException)
            {
                // Continue with any accessible case logs.
            }
        }

        return null;
    }

    public static ResidualLogData Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var samples = new List<ResidualSample>();
        var parser = new ResidualParser();
        parser.SampleParsed += samples.Add;

        foreach (var line in File.ReadLines(filePath))
            parser.ParseLine(line);

        return new ResidualLogData(Path.GetFullPath(filePath), samples);
    }

    private static IEnumerable<string> EnumerateCandidates(string casePath)
    {
        if (string.IsNullOrWhiteSpace(casePath) || !Directory.Exists(casePath))
            return [];

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workbenchLogDirectory = Path.Combine(casePath, "FoamWorkbenchLogs");

        if (Directory.Exists(workbenchLogDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(workbenchLogDirectory, "*.log",
                         SearchOption.TopDirectoryOnly))
                candidates.Add(file);
        }

        foreach (var file in Directory.EnumerateFiles(casePath, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("log.", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(file).Equals(".log", StringComparison.OrdinalIgnoreCase))
                candidates.Add(file);
        }

        return candidates
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
