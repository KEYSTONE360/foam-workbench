using System.Globalization;
using System.IO;
using System.Text;

namespace FoamWorkbench.Services;

public sealed record PythonResidualPlotArtifacts(
    string CsvPath,
    string ScriptPath,
    string PngPath,
    string SvgPath,
    int SampleCount,
    int FieldCount);

public sealed record PythonResidualPlotResult(
    PythonResidualPlotArtifacts Artifacts,
    ProcessResult Process);

public sealed class PythonResidualPlotService(OpenFoamService openFoam)
{
    public async Task<PythonResidualPlotResult> GenerateAsync(
        string casePath,
        IReadOnlyList<ResidualSample> samples,
        int recentSamplesPerField = 100,
        CancellationToken cancellationToken = default)
    {
        var artifacts = Prepare(casePath, samples);
        var plotDirectoryName = Path.GetFileName(Path.GetDirectoryName(artifacts.CsvPath))!;
        var command = string.Join(' ',
            "python3",
            ShellQuote($"{plotDirectoryName}/{Path.GetFileName(artifacts.ScriptPath)}"),
            ShellQuote($"{plotDirectoryName}/{Path.GetFileName(artifacts.CsvPath)}"),
            ShellQuote($"{plotDirectoryName}/{Path.GetFileName(artifacts.PngPath)}"),
            ShellQuote($"{plotDirectoryName}/{Path.GetFileName(artifacts.SvgPath)}"),
            Math.Max(10, recentSamplesPerField).ToString(CultureInfo.InvariantCulture));

        var result = await openFoam.RunCaseCommandAsync(casePath, command, cancellationToken);
        if (result.ExitCode != 0)
        {
            var message = result.Output.Contains("No module named", StringComparison.OrdinalIgnoreCase) &&
                          result.Output.Contains("matplotlib", StringComparison.OrdinalIgnoreCase)
                ? "Python matplotlib 라이브러리를 찾지 못했습니다. WSL에 python3-matplotlib를 설치하세요."
                : $"Python 잔차 그래프 생성에 실패했습니다.\n{result.Output.Trim()}";
            throw new InvalidOperationException(message);
        }

        if (!File.Exists(artifacts.PngPath) || !File.Exists(artifacts.SvgPath))
            throw new IOException("Python 실행은 완료됐지만 잔차 그래프 출력 파일을 찾지 못했습니다.");

        return new PythonResidualPlotResult(artifacts, result);
    }

    public static PythonResidualPlotArtifacts Prepare(
        string casePath,
        IReadOnlyList<ResidualSample> samples,
        DateTimeOffset? timestamp = null)
    {
        if (string.IsNullOrWhiteSpace(casePath) || !Directory.Exists(casePath))
            throw new DirectoryNotFoundException(casePath);
        if (samples.Count == 0)
            throw new InvalidOperationException("출력할 잔차 표본이 없습니다.");

        var directory = Path.Combine(casePath, "FoamWorkbenchPlots");
        Directory.CreateDirectory(directory);
        var stamp = (timestamp ?? DateTimeOffset.Now).ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var stem = $"residual-detail-{stamp}";
        var csvPath = Path.Combine(directory, $"{stem}.csv");
        var scriptPath = Path.Combine(directory, "render-residuals.py");
        var pngPath = Path.Combine(directory, $"{stem}.png");
        var svgPath = Path.Combine(directory, $"{stem}.svg");

        WriteCsv(csvPath, samples);
        File.WriteAllText(scriptPath, PlotScript, new UTF8Encoding(false));

        return new PythonResidualPlotArtifacts(
            csvPath,
            scriptPath,
            pngPath,
            svgPath,
            samples.Count,
            samples.Select(sample => sample.Field).Distinct(StringComparer.Ordinal).Count());
    }

    private static void WriteCsv(string filePath, IReadOnlyList<ResidualSample> samples)
    {
        using var writer = new StreamWriter(filePath, false, new UTF8Encoding(false));
        writer.WriteLine("sequence,field,initial,final,iterations");
        foreach (var sample in samples.OrderBy(sample => sample.Sequence))
        {
            writer.Write(sample.Sequence.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(EscapeCsv(sample.Field));
            writer.Write(',');
            writer.Write(sample.Initial.ToString("R", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(sample.Final.ToString("R", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.WriteLine(sample.Iterations.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string EscapeCsv(string value) =>
        $"\"{value.Replace("\"", "\"\"")}\"";

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\"'\"'")}'";

    private const string PlotScript = """
        import csv
        import math
        import sys
        from collections import OrderedDict

        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
        from matplotlib.ticker import LogLocator, LogFormatterSciNotation


        if len(sys.argv) != 5:
            raise SystemExit("usage: render-residuals.py input.csv output.png output.svg recent_count")

        csv_path, png_path, svg_path = sys.argv[1], sys.argv[2], sys.argv[3]
        recent_count = max(10, int(sys.argv[4]))
        fields = OrderedDict()

        with open(csv_path, newline="", encoding="utf-8") as stream:
            for row in csv.DictReader(stream):
                fields.setdefault(row["field"], []).append((
                    int(row["sequence"]),
                    max(float(row["initial"]), 1.0e-300),
                    max(float(row["final"]), 1.0e-300),
                    int(row["iterations"]),
                ))

        if not fields:
            raise SystemExit("no residual samples in input CSV")

        plt.style.use("dark_background")
        detail_rows = math.ceil(len(fields) / 2)
        figure = plt.figure(figsize=(18, 5.2 + detail_rows * 3.6), constrained_layout=True)
        figure.patch.set_facecolor("#0B121B")
        grid = figure.add_gridspec(1 + detail_rows, 2, height_ratios=[1.35] + [1.0] * detail_rows)
        overview = figure.add_subplot(grid[0, :])
        colors = list(plt.cm.tab10.colors)

        def decorate(axis):
            axis.set_facecolor("#0B121B")
            axis.set_yscale("log")
            axis.yaxis.set_major_locator(LogLocator(base=10.0))
            axis.yaxis.set_major_formatter(LogFormatterSciNotation(base=10.0))
            axis.yaxis.set_minor_locator(LogLocator(base=10.0, subs=tuple(range(2, 10))))
            axis.grid(True, which="major", color="#425267", alpha=0.72, linewidth=0.75)
            axis.grid(True, which="minor", color="#263548", alpha=0.42, linewidth=0.45)
            axis.tick_params(colors="#DCE7F2", labelsize=9)
            axis.set_axisbelow(True)

        decorate(overview)
        for index, (field, values) in enumerate(fields.items()):
            color = colors[index % len(colors)]
            overview.semilogy(
                [value[0] for value in values],
                [value[1] for value in values],
                color=color,
                linewidth=1.45,
                label=field,
            )
        overview.set_title("Full residual history — Initial residual", fontsize=14, weight="bold", color="#F3F6FA")
        overview.set_xlabel("Global solver sample", color="#AAB8C8")
        overview.set_ylabel("Residual", color="#AAB8C8")
        overview.legend(loc="upper right", ncol=min(5, len(fields)), framealpha=0.25, fontsize=9)

        for index, (field, values) in enumerate(fields.items()):
            row = 1 + index // 2
            column = index % 2
            axis = figure.add_subplot(grid[row, column])
            decorate(axis)
            view = values[-recent_count:]
            first_index = len(values) - len(view) + 1
            local_iterations = list(range(first_index, len(values) + 1))
            marker_spacing = max(1, len(view) // 18)
            color = colors[index % len(colors)]
            axis.semilogy(
                local_iterations,
                [value[1] for value in view],
                color=color,
                linewidth=1.7,
                marker="o",
                markersize=3.2,
                markevery=marker_spacing,
                label="Initial residual",
            )
            axis.semilogy(
                local_iterations,
                [value[2] for value in view],
                color="#F5C451",
                linewidth=1.25,
                linestyle="--",
                label="Final residual",
            )
            axis.set_title(f"{field} — last {len(view)} solves", fontsize=12, weight="bold", color="#F3F6FA")
            axis.set_xlabel("Field solve index", color="#AAB8C8")
            axis.set_ylabel("Residual", color="#AAB8C8")
            latest = view[-1]
            axis.text(
                0.985,
                0.965,
                f"latest initial  {latest[1]:.5e}\nlatest final    {latest[2]:.5e}\nlinear iterations  {latest[3]}",
                transform=axis.transAxes,
                horizontalalignment="right",
                verticalalignment="top",
                fontsize=9,
                color="#E8F0F7",
                bbox=dict(boxstyle="round,pad=0.45", facecolor="#151F2C", edgecolor="#52647A", alpha=0.92),
            )
            axis.legend(loc="lower left", framealpha=0.25, fontsize=8)

        if len(fields) % 2:
            unused = figure.add_subplot(grid[-1, 1])
            unused.axis("off")

        figure.suptitle(
            f"OpenFOAM residual diagnostics | {sum(len(values) for values in fields.values())} samples | {len(fields)} fields",
            fontsize=17,
            weight="bold",
            color="#F3F6FA",
        )
        figure.savefig(png_path, dpi=220, facecolor=figure.get_facecolor(), bbox_inches="tight")
        figure.savefig(svg_path, facecolor=figure.get_facecolor(), bbox_inches="tight")
        print(f"PNG={png_path}")
        print(f"SVG={svg_path}")
        """;
}
