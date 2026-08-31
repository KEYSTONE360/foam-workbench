using System.Globalization;
using System.IO;
using System.Text;

namespace FoamWorkbench.Services;

public sealed class PorousSweepService(
    PorousCaseGenerator generator,
    PorousSimulationService simulation)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public async Task<(string CsvPath, IReadOnlyList<PorousSweepRow> Rows)> RunAsync(
        PorousCaseSettings baseSettings,
        PorousSweepRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.LayerIndex < 0 || request.LayerIndex >= baseSettings.Layers.Count)
            throw new ArgumentOutOfRangeException(nameof(request.LayerIndex));
        if (request.Steps < 2) throw new ArgumentOutOfRangeException(nameof(request.Steps));
        if (request.StartPermeability <= 0 || request.EndPermeability <= 0 ||
            !double.IsFinite(request.StartPermeability) || !double.IsFinite(request.EndPermeability))
            throw new ArgumentException("Sweep permeability range must be finite and greater than 0 m².");

        var sweepRoot = Path.Combine(
            Path.GetFullPath(baseSettings.OutputRootPath),
            $"{baseSettings.ProjectName}_Sweep_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(sweepRoot);
        var rows = new List<PorousSweepRow>();
        for (var step = 0; step < request.Steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fraction = request.Steps == 1 ? 0 : step / (double)(request.Steps - 1);
            var logK = Math.Log10(request.StartPermeability) +
                       (Math.Log10(request.EndPermeability) - Math.Log10(request.StartPermeability)) * fraction;
            var permeability = Math.Pow(10, logK);
            var layers = baseSettings.Layers.Select(layer => layer.Clone()).ToArray();
            var selected = layers[request.LayerIndex];
            selected.PermeabilityType = PorousPermeabilityType.Isotropic;
            selected.Permeability = permeability;
            selected.ParameterSource = PorousParameterSource.UserDefined;
            var stepName = $"step_{step + 1:000}_k_{permeability.ToString("0.000E+00", Inv).Replace('+', 'p').Replace('-', 'm')}";
            var settings = baseSettings.CloneWith(layers, sweepRoot, stepName);
            PorousGenerationResult generated;
            PorousResultSummary? result = null;
            if (request.RunSolver)
            {
                generated = await simulation.GenerateMeshAndOptionallySolveAsync(
                    settings, runMesh: true, runSolver: true, cancellationToken);
                result = PorousResultProcessor.Load(generated.CasePath, settings);
            }
            else
            {
                generated = generator.Generate(settings);
            }

            var q = result is null ? double.NaN : Math.Abs(result.OutletFlowRate);
            var dp = result?.PressureDropPa ?? double.NaN;
            var cfdK = EstimateCfdPermeability(settings, generated.Analytical, q, dp);
            var error = double.IsFinite(cfdK)
                ? Math.Abs(cfdK - generated.Analytical.EquivalentPermeability) /
                  generated.Analytical.EquivalentPermeability * 100
                : double.NaN;
            rows.Add(new PorousSweepRow(
                step + 1,
                generated.CasePath,
                $"{selected.Name}.permeability",
                permeability,
                q,
                dp,
                generated.Analytical.EquivalentPermeability,
                cfdK,
                error,
                generated.Analytical.Bottleneck.ResistanceFraction));
        }

        var csvPath = Path.Combine(sweepRoot, "parameter_sweep.csv");
        WriteCsv(csvPath, rows);
        return (csvPath, rows);
    }

    private static double EstimateCfdPermeability(
        PorousCaseSettings settings,
        DarcyAnalysisResult analytical,
        double flowRate,
        double pressureDrop)
    {
        if (!double.IsFinite(flowRate) || !double.IsFinite(pressureDrop)) return double.NaN;
        var width = PorousUnitConverter.MillimetresToMetres(settings.DomainWidthMm);
        var depth = PorousUnitConverter.MillimetresToMetres(settings.TargetCellSizeMm);
        var superficialVelocity = flowRate / (width * depth);
        var gravityDrive = settings.GravityEnabled
            ? settings.Density * Math.Abs(settings.GravityY) * analytical.TotalThicknessMetres
            : 0;
        var drivingPressure = Math.Abs(pressureDrop) + gravityDrive;
        if (drivingPressure <= 1e-30) return double.NaN;
        return superficialVelocity * settings.DynamicViscosity * analytical.TotalThicknessMetres / drivingPressure;
    }

    private static void WriteCsv(string path, IReadOnlyList<PorousSweepRow> rows)
    {
        var text = new StringBuilder("parameter,permeability,flowRate,pressureDrop,analyticalKeff,CFDKeff,error,bottleneckFraction,casePath\n");
        foreach (var row in rows)
            text.AppendLine(string.Join(',',
                Csv(row.Parameter), F(row.Permeability), F(row.FlowRate), F(row.PressureDrop),
                F(row.AnalyticalKeff), F(row.CfdKeff), F(row.ErrorPercent),
                F(row.BottleneckFraction), Csv(row.CasePath)));
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string F(double value) => double.IsFinite(value) ? value.ToString("G17", Inv) : "";
}
