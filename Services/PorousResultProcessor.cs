using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FoamWorkbench.Services;

public static class PorousResultProcessor
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Regex NumberRegex = new(
        @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?",
        RegexOptions.Compiled);

    public static PorousResultSummary Load(string casePath, PorousCaseSettings settings)
    {
        var resultTimes = FindResultTimes(casePath);
        var finalTime = resultTimes.LastOrDefault(double.NaN);
        var inletFlow = ReadLastScalar(casePath, "inletFlow");
        var outletFlow = ReadLastScalar(casePath, "outletFlow");
        var inletPressure = ReadLastScalar(casePath, "inletPressure") * settings.Density;
        var outletPressure = ReadLastScalar(casePath, "outletPressure") * settings.Density;
        PorousFlowBalance? balance = null;
        if (double.IsFinite(inletFlow) && double.IsFinite(outletFlow))
            balance = PorousPhysics.CalculateFlowBalance(inletFlow, outletFlow);

        DarcyAnalysisResult? analytical = null;
        if (PorousPhysics.Validate(settings).IsValid)
            analytical = PorousPhysics.CalculateAnalytical(settings);
        var layerResults = new List<PorousLayerResult>();
        foreach (var layer in settings.Layers)
        {
            var values = ReadLastValues(casePath, $"average_{layer.Name}");
            var pressure = values.Length >= 2 ? values[1] * settings.Density : double.NaN;
            var pressureIn = ReadLastScalar(casePath, $"pressureIn_{layer.Name}") * settings.Density;
            var pressureOut = ReadLastScalar(casePath, $"pressureOut_{layer.Name}") * settings.Density;
            var ux = values.Length >= 5 ? values[2] : double.NaN;
            var uy = values.Length >= 5 ? values[3] : double.NaN;
            var uz = values.Length >= 5 ? values[4] : double.NaN;
            var magnitude = double.IsFinite(ux) && double.IsFinite(uy) && double.IsFinite(uz)
                ? Math.Sqrt(ux * ux + uy * uy + uz * uz)
                : double.NaN;
            var residence = layer.Thickness is > 0 && double.IsFinite(uy)
                ? PorousPhysics.NominalResidenceTime(
                    PorousUnitConverter.MillimetresToMetres(layer.Thickness.Value), uy)
                : double.NaN;
            var fraction = analytical?.Layers.FirstOrDefault(item => item.LayerId == layer.Id)?.ResistanceFraction
                           ?? double.NaN;
            layerResults.Add(new PorousLayerResult(
                layer.Id, layer.DisplayNameEn, pressure, pressureIn, pressureOut,
                pressureIn - pressureOut, magnitude, uy, residence, fraction));
        }

        var centerline = CreateCenterlineCsv(casePath, settings);
        var pressureDrop = inletPressure - outletPressure;
        var totalThickness = settings.Layers.Sum(layer =>
            PorousUnitConverter.MillimetresToMetres(layer.Thickness ?? 0));
        var sliceArea = PorousUnitConverter.MillimetresToMetres(settings.DomainWidthMm) *
                        PorousUnitConverter.MillimetresToMetres(settings.TargetCellSizeMm);
        var inletVelocity = ReadLastVectorMagnitude(casePath, "inletVelocity");
        if (!double.IsFinite(inletVelocity) && sliceArea > 0 && double.IsFinite(inletFlow))
            inletVelocity = Math.Abs(inletFlow) / sliceArea;
        var outletVelocity = ReadLastVectorMagnitude(casePath, "outletVelocity");
        if (!double.IsFinite(outletVelocity) && sliceArea > 0 && double.IsFinite(outletFlow))
            outletVelocity = Math.Abs(outletFlow) / sliceArea;
        var throughVelocity = sliceArea > 0 && double.IsFinite(outletFlow)
            ? Math.Abs(outletFlow) / sliceArea
            : double.NaN;
        // 1-D saturated Darcy balance for the configured vertical -Y stack:
        // U = k/mu * (deltaP/L + rho*|gy|). This defines the reported CFD k_eff.
        var totalDrivingPressure = pressureDrop + settings.Density * Math.Abs(settings.GravityY) * totalThickness;
        var cfdKeff = double.IsFinite(throughVelocity) && throughVelocity >= 0 &&
                      double.IsFinite(totalDrivingPressure) && totalDrivingPressure > 1e-30 && totalThickness > 0
            ? settings.DynamicViscosity * throughVelocity * totalThickness / totalDrivingPressure
            : double.NaN;
        var gravityMagnitude = Math.Sqrt(settings.GravityX * settings.GravityX +
                                         settings.GravityY * settings.GravityY +
                                         settings.GravityZ * settings.GravityZ);
        var cfdHydraulic = double.IsFinite(cfdKeff) && settings.DynamicViscosity > 0
            ? cfdKeff * settings.Density * gravityMagnitude / settings.DynamicViscosity
            : double.NaN;
        var cfdDifference = analytical is not null && double.IsFinite(cfdKeff) &&
                            analytical.EquivalentPermeability > 0
            ? Math.Abs(cfdKeff - analytical.EquivalentPermeability) /
              analytical.EquivalentPermeability * 100
            : double.NaN;
        var residualValues = ReadLastValues(casePath, "residuals");
        var residualMaximum = residualValues.Length > 1
            ? residualValues.Skip(1).Select(Math.Abs).DefaultIfEmpty(double.NaN).Max()
            : double.NaN;
        var expectedInletVelocity = settings.FlowMode == PorousFlowMode.RainfallFlux
            ? PorousUnitConverter.MillimetresPerHourToMetresPerSecond(settings.RainfallMmPerHour)
            : double.NaN;
        var inletVelocityPreserved = settings.FlowMode != PorousFlowMode.RainfallFlux ||
                                     double.IsFinite(inletVelocity) && inletVelocity >= expectedInletVelocity * 0.5;
        var sanityMessages = new List<string>();
        if (!double.IsFinite(finalTime) || finalTime <= 0)
            sanityMessages.Add("No calculated result timestep exists; ParaView may be showing the initial field at Time=0.");
        if (!inletVelocityPreserved)
            sanityMessages.Add($"Scenario A inlet velocity was not preserved: expected {expectedInletVelocity:G8} m/s, actual {inletVelocity:G8} m/s. Check boundary conditions.");
        if (settings.FlowMode == PorousFlowMode.RainfallFlux &&
            double.IsFinite(outletVelocity) && outletVelocity < expectedInletVelocity * 0.5)
            sanityMessages.Add($"Scenario A outlet velocity is too small: expected order {expectedInletVelocity:G8} m/s, actual {outletVelocity:G8} m/s.");
        if (balance is { Pass: false })
            sanityMessages.Add($"Inlet/outlet volume-flow imbalance is {balance.DifferencePercent:G6}% (target <= 1%).");

        var finalTimeReached = double.IsFinite(finalTime) &&
                               finalTime >= settings.EndTime - Math.Max(1e-9, Math.Abs(settings.EndTime) * 1e-9);
        var residualConverged = double.IsFinite(residualMaximum) && residualMaximum <= 1e-6;
        var simulationStatus = finalTimeReached && residualConverged &&
                               balance is { Pass: true } && inletVelocityPreserved
            ? PorousSimulationStatus.Converged
            : PorousSimulationStatus.NotConverged;
        return new PorousResultSummary
        {
            CasePath = casePath,
            InletFlowRate = inletFlow,
            OutletFlowRate = outletFlow,
            InletPressurePa = inletPressure,
            OutletPressurePa = outletPressure,
            CfdEquivalentPermeability = cfdKeff,
            CfdHydraulicConductivity = cfdHydraulic,
            CfdAnalyticalDifferencePercent = cfdDifference,
            SimulationStatus = simulationStatus,
            FinalTime = finalTime,
            ResultDirectoryCount = resultTimes.Count,
            FinalResidualMaximum = residualMaximum,
            InletAverageVelocity = inletVelocity,
            OutletAverageVelocity = outletVelocity,
            ExpectedInletVelocity = expectedInletVelocity,
            InletVelocityPreserved = inletVelocityPreserved,
            SanityMessages = sanityMessages,
            FlowBalance = balance,
            Layers = layerResults,
            CenterlineCsvPath = centerline
        };
    }

    public static double ReadLastScalar(string casePath, string functionName)
    {
        var values = ReadLastValues(casePath, functionName);
        return values.Length >= 2 ? values[^1] : double.NaN;
    }

    public static double ReadLastVectorMagnitude(string casePath, string functionName)
    {
        var values = ReadLastValues(casePath, functionName);
        if (values.Length < 4) return double.NaN;
        var x = values[^3];
        var y = values[^2];
        var z = values[^1];
        return Math.Sqrt(x * x + y * y + z * z);
    }

    public static IReadOnlyList<double> FindResultTimes(string casePath)
    {
        if (!Directory.Exists(casePath)) return [];
        return Directory.EnumerateDirectories(casePath)
            .Select(Path.GetFileName)
            .Where(name => double.TryParse(name, NumberStyles.Float, Inv, out var time) && time > 0 &&
                           (File.Exists(Path.Combine(casePath, name!, "U")) ||
                            File.Exists(Path.Combine(casePath, name!, "p")) ||
                            File.Exists(Path.Combine(casePath, name!, "p_rgh"))))
            .Select(name => double.Parse(name!, NumberStyles.Float, Inv))
            .OrderBy(time => time)
            .ToArray();
    }

    public static string CreateCenterlineCsv(string casePath, PorousCaseSettings settings)
    {
        var root = Path.Combine(casePath, "postProcessing", "centerline");
        if (!Directory.Exists(root)) return "";
        var source = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains("centerline", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (source is null) return "";

        var output = Path.Combine(casePath, "centerline_profile.csv");
        var text = new StringBuilder("position,pressure,velocityMagnitude,Uy,layerId\n");
        foreach (var line in File.ReadLines(source))
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var values = ParseNumbers(line);
            if (values.Length < 6) continue;
            var position = values[0];
            var p = values[1] * settings.Density;
            var ux = values[2];
            var uy = values[3];
            var uz = values[4];
            var magnitude = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            var layerId = LayerAtPosition(settings.Layers, position);
            text.AppendLine(string.Join(',',
                F(position), F(p), F(magnitude), F(uy), layerId.ToString(Inv)));
        }
        File.WriteAllText(output, text.ToString(), new UTF8Encoding(false));
        return output;
    }

    private static double[] ReadLastValues(string casePath, string functionName)
    {
        var root = Path.Combine(casePath, "postProcessing", functionName);
        if (!Directory.Exists(root)) return [];
        var file = Directory.EnumerateFiles(root, "*.dat", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (file is null) return [];
        var line = File.ReadLines(file).LastOrDefault(value =>
            !string.IsNullOrWhiteSpace(value) && !value.TrimStart().StartsWith('#'));
        return line is null ? [] : ParseNumbers(line);
    }

    private static double[] ParseNumbers(string value) => NumberRegex.Matches(value)
        .Select(match => double.Parse(match.Value, NumberStyles.Float, Inv)).ToArray();

    private static int LayerAtPosition(IReadOnlyList<PorousLayer> layers, double position)
    {
        var y = 0.0;
        foreach (var layer in layers.Reverse())
        {
            y += PorousUnitConverter.MillimetresToMetres(layer.Thickness ?? 0);
            if (position <= y + 1e-12) return layer.Id;
        }
        return layers.Count == 0 ? 0 : layers[^1].Id;
    }

    private static string F(double value) => value.ToString("G17", Inv);
}
