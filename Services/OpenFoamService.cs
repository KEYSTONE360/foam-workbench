using System.Diagnostics;
using System.IO;
using System.Text;

namespace FoamWorkbench.Services;

public sealed class OpenFoamService(AppSettings settings, ProcessRunner runner)
{
    public async Task<RuntimeProbe> ProbeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var command = settings.Backend == RuntimeBackend.Wsl
                ? $"if [ -f {Quote(settings.OpenFoamBashrc)} ]; then source {Quote(settings.OpenFoamBashrc)}; fi; " +
                  "foamVersion 2>&1 || true; command -v blockMesh || true; command -v foamRun || true; " +
                  "command -v surfaceFeatures || true; command -v snappyHexMesh || true; command -v gmsh || true"
                : "foamVersion 2>&1 || true; command -v blockMesh || true; command -v gmsh || true";

            var result = await runner.RunAsync(CreateRawStartInfo(command), cancellationToken);
            var lines = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToArray();
            var version = lines.FirstOrDefault(line =>
                line.Contains("OpenFOAM-", StringComparison.OrdinalIgnoreCase)) ?? "";
            var blockMesh = lines.FirstOrDefault(line =>
                line.Replace('\\', '/').EndsWith("/blockMesh", StringComparison.Ordinal)) ?? "";
            var gmsh = lines.FirstOrDefault(line =>
                line.Replace('\\', '/').EndsWith("/gmsh", StringComparison.Ordinal)) ?? "";
            return new RuntimeProbe
            {
                IsAvailable = result.ExitCode == 0 &&
                              !string.IsNullOrWhiteSpace(blockMesh) &&
                              !string.IsNullOrWhiteSpace(gmsh),
                Version = string.IsNullOrWhiteSpace(version) ? "감지 안 됨" : version,
                Details = result.Output.Trim()
            };
        }
        catch (Exception ex)
        {
            return new RuntimeProbe { IsAvailable = false, Details = ex.Message };
        }
    }

    public Task<ProcessResult> RunCaseCommandAsync(
        string windowsCasePath,
        string command,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(windowsCasePath))
            throw new DirectoryNotFoundException(windowsCasePath);

        var casePath = settings.Backend == RuntimeBackend.Wsl ? ToWslPath(windowsCasePath) : "/case";
        var shell = new StringBuilder("set -o pipefail; ");
        if (settings.Backend == RuntimeBackend.Wsl)
            shell.Append($"source {Quote(settings.OpenFoamBashrc)}; ");
        shell.Append($"cd {Quote(casePath)}; {command}");
        return runner.RunAsync(CreateCaseStartInfo(shell.ToString(), windowsCasePath), cancellationToken);
    }

    public Task<ProcessResult> RunRuntimeCommandAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        var shell = new StringBuilder("set -o pipefail; ");
        if (settings.Backend == RuntimeBackend.Wsl)
            shell.Append($"source {Quote(settings.OpenFoamBashrc)}; ");
        shell.Append(command);
        return runner.RunAsync(CreateRawStartInfo(shell.ToString()), cancellationToken);
    }

    public Task<ProcessResult> ReadRuntimeCatalogAsync(
        string option,
        CancellationToken cancellationToken = default)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "-solvers", "-functionObjects", "-scalarBCs", "-vectorBCs", "-fvModels", "-fvConstraints",
            "-tables", "-switches", "-all"
        };
        if (!allowed.Contains(option))
            throw new ArgumentOutOfRangeException(nameof(option), "지원하지 않는 OpenFOAM 카탈로그 범주입니다.");
        return RunRuntimeCommandAsync($"foamToC {option}", cancellationToken);
    }

    public Task<ProcessResult> ReadRuntimeInfoAsync(
        string selection,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(selection) ||
            selection.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or ':' or '-' or '.' or '<' or '>')))
            throw new ArgumentException("OpenFOAM 항목 이름에 허용되지 않는 문자가 있습니다.", nameof(selection));
        // foamInfo can find more than one matching header and prompt for a number.
        // Feed the primary match so the WPF process remains non-interactive.
        return RunRuntimeCommandAsync($"printf '1\\n' | foamInfo -all {Quote(selection)}", cancellationToken);
    }

    public string ResolveSolverCommand(CaseInfo info, bool parallel)
    {
        var application = string.IsNullOrWhiteSpace(info.Application) ? "foamRun" : info.Application;
        var command = application;

        if (application == "foamRun" && string.IsNullOrWhiteSpace(info.SolverModule))
            command = "foamRun";

        if (parallel)
            command = $"mpirun -np {Math.Max(1, settings.ProcessCount)} {command} -parallel";

        return command;
    }

    public static string ToWslPath(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
            throw new ArgumentException("드라이브 문자가 포함된 Windows 경로가 필요합니다.", nameof(windowsPath));

        var drive = char.ToLowerInvariant(root[0]);
        var remainder = full[root.Length..].Replace('\\', '/');
        return $"/mnt/{drive}/{remainder}";
    }

    public void OpenParaView(string windowsCasePath)
    {
        if (!File.Exists(settings.ParaViewPath))
            throw new FileNotFoundException("ParaView 실행 파일을 찾을 수 없습니다.", settings.ParaViewPath);

        var marker = Directory.EnumerateFiles(windowsCasePath, "*.foam", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        if (marker is null)
        {
            marker = Path.Combine(windowsCasePath, $"{new DirectoryInfo(windowsCasePath).Name}.foam");
            File.WriteAllText(marker, "");
        }

        var startupScript = PrepareParaViewStartupScript(windowsCasePath, marker);
        Process.Start(new ProcessStartInfo
        {
            FileName = settings.ParaViewPath,
            UseShellExecute = false,
            ArgumentList = { $"--script={startupScript}" }
        });
    }

    public static string PrepareParaViewStartupScript(string windowsCasePath, string? marker = null)
    {
        marker ??= Directory.EnumerateFiles(windowsCasePath, "*.foam", SearchOption.TopDirectoryOnly)
            .FirstOrDefault() ?? Path.Combine(windowsCasePath, $"{new DirectoryInfo(windowsCasePath).Name}.foam");
        if (!File.Exists(marker)) File.WriteAllText(marker, "");

        var caseValue = marker.Replace('\\', '/').Replace("'", "\\'");
        var script = $$"""
from paraview.simple import *

case_marker = '{{caseValue}}'
reader = OpenFOAMReader(registrationName='FOAM Workbench latest result', FileName=case_marker)
available_fields = list(reader.CellArrays.Available)
reader.CellArrays = available_fields
available_regions = list(reader.MeshRegions.Available)
visible_regions = [name for name in available_regions if name == 'internalMesh' or name.startswith('patch/')]
if visible_regions:
    reader.MeshRegions = visible_regions

times = list(reader.TimestepValues)
if times and max(times) > 0:
    reader.SkipZeroTime = 1
view = GetActiveViewOrCreate('RenderView')
display = Show(reader, view)
scene = GetAnimationScene()
scene.UpdateAnimationUsingDataTimeSteps()
if times:
    latest_time = max(times)
    scene.AnimationTime = latest_time
    UpdatePipeline(time=latest_time, proxy=reader)
if 'U' in available_fields:
    ColorBy(display, ('CELLS', 'U', 'Magnitude'))
display.RescaleTransferFunctionToDataRange(True, False)
view.ResetCamera()
Render()
""";
        var scriptPath = Path.Combine(windowsCasePath, "FoamWorkbenchParaViewLatest.py");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
        return scriptPath;
    }

    private ProcessStartInfo CreateCaseStartInfo(string shellCommand, string windowsCasePath)
    {
        if (settings.Backend == RuntimeBackend.Wsl) return CreateRawStartInfo(shellCommand);

        var info = BaseStartInfo("docker.exe");
        info.ArgumentList.Add("run");
        info.ArgumentList.Add("--rm");
        info.ArgumentList.Add("-i");
        info.ArgumentList.Add("-v");
        info.ArgumentList.Add($"{Path.GetFullPath(windowsCasePath)}:/case");
        info.ArgumentList.Add("-w");
        info.ArgumentList.Add("/case");
        info.ArgumentList.Add(settings.DockerImage);
        info.ArgumentList.Add("bash");
        info.ArgumentList.Add("-lc");
        info.ArgumentList.Add(shellCommand);
        return info;
    }

    private ProcessStartInfo CreateRawStartInfo(string shellCommand)
    {
        if (settings.Backend == RuntimeBackend.Wsl)
        {
            var info = BaseStartInfo("wsl.exe");
            info.ArgumentList.Add("-d");
            info.ArgumentList.Add(settings.WslDistribution);
            info.ArgumentList.Add("--");
            info.ArgumentList.Add("bash");
            info.ArgumentList.Add("-lc");
            info.ArgumentList.Add(shellCommand);
            return info;
        }

        var docker = BaseStartInfo("docker.exe");
        docker.ArgumentList.Add("run");
        docker.ArgumentList.Add("--rm");
        docker.ArgumentList.Add(settings.DockerImage);
        docker.ArgumentList.Add("bash");
        docker.ArgumentList.Add("-lc");
        docker.ArgumentList.Add(shellCommand);
        return docker;
    }

    private static ProcessStartInfo BaseStartInfo(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    };

    private static string Quote(string value) => $"'{value.Replace("'", "'\"'\"'")}'";

}
