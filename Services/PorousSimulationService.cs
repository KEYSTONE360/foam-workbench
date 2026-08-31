using System.IO;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FoamWorkbench.Services;

public sealed class PorousPipelineException : Exception
{
    public string Stage { get; }
    public string Command { get; }
    public int ExitCode { get; }
    public string LogTail { get; }

    public PorousPipelineException(string stage, string command, ProcessResult result)
        : base($"{stage} failed.\nCommand: {command}\nExit code: {result.ExitCode}\n\nLast output:\n{Tail(result.Output, 35)}")
    {
        Stage = stage;
        Command = command;
        ExitCode = result.ExitCode;
        LogTail = Tail(result.Output, 35);
    }

    private static string Tail(string text, int lines) => string.Join(Environment.NewLine,
        text.Replace("\r\n", "\n").Split('\n').TakeLast(lines));
}

public sealed class PorousSimulationService(
    OpenFoamService openFoam,
    PorousCaseGenerator generator)
{
    public async Task<PorousGenerationResult> GenerateMeshAndOptionallySolveAsync(
        PorousCaseSettings settings,
        bool runMesh,
        bool runSolver,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        var generated = generator.Generate(settings);
        progress?.Report("CASE GENERATED");
        if (!runMesh) return generated;

        progress?.Report("MESH GENERATING · blockMesh");
        await RunStageAsync(generated.CasePath, "BLOCKMESH", "blockMesh", cancellationToken);
        progress?.Report("MESH GENERATED");
        await RunStageAsync(generated.CasePath, "PERMEABILITY FIELD", "setFields", cancellationToken);
        VerifyCellZones(generated.CasePath, generated.ZoneCellCounts.Keys);
        progress?.Report("CELL ZONES VERIFIED");
        await RunStageAsync(generated.CasePath, "CHECKMESH", "checkMesh -allTopology -allGeometry", cancellationToken);
        progress?.Report("MESH VALIDATED");
        if (runSolver)
        {
            progress?.Report("SOLVER RUNNING · foamRun");
            await RunStageAsync(generated.CasePath, "SOLVER", "foamRun", cancellationToken);
            progress?.Report("POSTPROCESSING");
            PublishVisualizationFieldsToResultTimes(generated.CasePath);
            progress?.Report("SOLVER FINISHED · result timestep detected");
        }
        return generated;
    }

    public async Task RunExistingGeneratedCaseAsync(
        string casePath,
        bool runMesh,
        bool runSolver,
        CancellationToken cancellationToken = default)
    {
        if (runMesh)
        {
            await RunStageAsync(casePath, "BLOCKMESH", "blockMesh", cancellationToken);
            await RunStageAsync(casePath, "PERMEABILITY FIELD", "setFields", cancellationToken);
            await RunStageAsync(casePath, "CELL ZONE", "checkMesh -constant", cancellationToken);
            await RunStageAsync(casePath, "CHECKMESH", "checkMesh -allTopology -allGeometry", cancellationToken);
        }
        if (runSolver)
        {
            await RunStageAsync(casePath, "SOLVER", "foamRun", cancellationToken);
            PublishVisualizationFieldsToResultTimes(casePath);
        }
    }

    private async Task RunStageAsync(
        string casePath,
        string stage,
        string command,
        CancellationToken cancellationToken)
    {
        var result = await openFoam.RunCaseCommandAsync(casePath, command, cancellationToken);
        var commandName = command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var logName = commandName.Equals("foamRun", StringComparison.Ordinal)
            ? "log.foamRun"
            : $"log.{commandName}";
        File.WriteAllText(Path.Combine(casePath, logName), result.Output, new UTF8Encoding(false));
        if (result.ExitCode != 0) throw new PorousPipelineException(stage, command, result);
    }

    public static IReadOnlyDictionary<string, int> ReadCellZoneCounts(string casePath)
    {
        var path = Path.Combine(casePath, "constant", "polyMesh", "cellZones");
        if (!File.Exists(path)) throw new FileNotFoundException("OpenFOAM cellZones file was not generated.", path);
        var text = File.ReadAllText(path);
        var matches = Regex.Matches(text,
            @"(?ms)^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{\s*cellLabels\s+List<label>\s+(?<count>\d+)");
        return matches.Cast<Match>().ToDictionary(
            match => match.Groups["name"].Value,
            match => int.Parse(match.Groups["count"].Value),
            StringComparer.Ordinal);
    }

    public static void VerifyCellZones(string casePath, IEnumerable<string> expectedZones)
    {
        var actual = ReadCellZoneCounts(casePath);
        var missing = expectedZones.Where(zone => !actual.TryGetValue(zone, out var count) || count <= 0).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException(
                "CELL ZONE verification failed. Missing or empty zones: " + string.Join(", ", missing));
    }

    /// <summary>
    /// Publishes static, visualization-only material fields beside U/p in every
    /// solver result time. OpenFOAM does not auto-write fields that are not read
    /// by the equation, so leaving these only in 0/ makes ParaView's latest-time
    /// Color By list unreliable. The numeric values remain input properties.
    /// </summary>
    public static IReadOnlyList<string> PublishVisualizationFieldsToResultTimes(string casePath)
    {
        var zero = Path.Combine(casePath, "0");
        var sourceFields = new[] { "layerId", "permeability" }
            .Select(name => (Name: name, Path: Path.Combine(zero, name)))
            .Where(item => File.Exists(item.Path))
            .ToArray();
        if (sourceFields.Length == 0 || !Directory.Exists(casePath)) return [];

        var written = new List<string>();
        var resultTimes = Directory.EnumerateDirectories(casePath)
            .Select(path => (Path: path, Name: Path.GetFileName(path)))
            .Where(item => double.TryParse(item.Name, NumberStyles.Float, CultureInfo.InvariantCulture,
                               out var time) && time > 0 &&
                           (File.Exists(Path.Combine(item.Path, "U")) ||
                            File.Exists(Path.Combine(item.Path, "p")) ||
                            File.Exists(Path.Combine(item.Path, "p_rgh"))))
            .OrderBy(item => double.Parse(item.Name, NumberStyles.Float, CultureInfo.InvariantCulture));

        foreach (var time in resultTimes)
        {
            foreach (var field in sourceFields)
            {
                var text = File.ReadAllText(field.Path);
                text = Regex.Replace(text, "(?m)^(\\s*location\\s+)\"0\";",
                    $"$1\"{time.Name}\";");
                var destination = Path.Combine(time.Path, field.Name);
                File.WriteAllText(destination, text, new UTF8Encoding(false));
                written.Add(destination);
            }
        }
        return written;
    }
}
