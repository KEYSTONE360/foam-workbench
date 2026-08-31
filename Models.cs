using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace FoamWorkbench;

public enum RuntimeBackend
{
    Wsl,
    Docker
}

public enum CadLengthUnit
{
    Millimetre,
    Metre
}

public enum CadAnalysisType
{
    ExternalFlow,
    InternalFluidVolume
}

public enum FlowAxis
{
    PositiveX,
    NegativeX,
    PositiveY,
    NegativeY,
    PositiveZ,
    NegativeZ
}

public enum TurbulenceChoice
{
    KOmegaSst,
    Laminar
}

public sealed class CadProjectSettings
{
    public string CadFilePath { get; init; } = "";
    public string OutputRootPath { get; init; } = "";
    public string ProjectName { get; init; } = "NewCfdProject";
    public CadLengthUnit CadUnit { get; init; } = CadLengthUnit.Millimetre;
    public CadAnalysisType AnalysisType { get; init; } = CadAnalysisType.ExternalFlow;
    public FlowAxis FlowAxis { get; init; } = FlowAxis.PositiveX;
    public TurbulenceChoice Turbulence { get; init; } = TurbulenceChoice.KOmegaSst;
    public double Velocity { get; init; } = 10;
    public double KinematicViscosity { get; init; } = 1.5e-5;
    public double TurbulenceIntensityPercent { get; init; } = 5;
    public double TurbulenceLengthScale { get; init; } = 0.07;
    public double CadSurfaceSize { get; init; } = 5;
    public double BaseCellSize { get; init; } = 0.1;
    public int SurfaceRefinementMin { get; init; } = 2;
    public int SurfaceRefinementMax { get; init; } = 3;
    public int FeatureRefinementLevel { get; init; } = 3;
    public int BoundaryLayerCount { get; init; } = 3;
    public double LayerExpansionRatio { get; init; } = 1.2;
    public double FinalLayerThickness { get; init; } = 0.3;
    public int MaxGlobalCells { get; init; } = 4_000_000;
    public double UpstreamLengths { get; init; } = 3;
    public double DownstreamLengths { get; init; } = 8;
    public double SideLengths { get; init; } = 4;
    public int EndTime { get; init; } = 1000;
    public int WriteInterval { get; init; } = 100;
    public int ProcessCount { get; init; } = 4;
    public string? FluidPointText { get; init; }

    // OpenFOAM functionObjects. These options only generate dictionaries;
    // every calculation is performed by the linked OpenFOAM runtime.
    public bool CalculateResiduals { get; init; } = true;
    public bool CalculateForces { get; init; } = true;
    public bool CalculateForceCoefficients { get; init; } = true;
    public bool CalculateWallShearStress { get; init; } = true;
    public bool CalculateYPlus { get; init; }
    public bool CalculateQCriterion { get; init; } = true;
    public bool CalculateVorticity { get; init; } = true;
    public bool CalculateTurbulenceIntensity { get; init; }
    public bool CalculateFieldAverage { get; init; }
    public string ForcePatches { get; init; } = "model.*";
    public double Density { get; init; } = 1.225;
    public double ReferenceArea { get; init; } = 1;
    public double ReferenceLength { get; init; } = 1;
    public string CentreOfRotationText { get; init; } = "0 0 0";
    public string DragDirectionText { get; init; } = "1 0 0";
    public string LiftDirectionText { get; init; } = "0 0 1";
    public string PitchAxisText { get; init; } = "0 1 0";
    public string AveragedFields { get; init; } = "U p";
    public string CustomFunctionObjects { get; init; } = "";
}

public sealed class MeshCalculationPreset
{
    public int SchemaVersion { get; init; } = 1;
    public string Name { get; init; } = "Mesh and calculation preset";
    public DateTimeOffset SavedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public CadLengthUnit CadUnit { get; init; } = CadLengthUnit.Millimetre;
    public CadAnalysisType AnalysisType { get; init; } = CadAnalysisType.ExternalFlow;
    public FlowAxis FlowAxis { get; init; } = FlowAxis.PositiveX;
    public TurbulenceChoice Turbulence { get; init; } = TurbulenceChoice.KOmegaSst;
    public double Velocity { get; init; } = 10;
    public double KinematicViscosity { get; init; } = 1.5e-5;
    public double TurbulenceIntensityPercent { get; init; } = 5;
    public double TurbulenceLengthScale { get; init; } = 0.07;
    public double CadSurfaceSize { get; init; } = 5;
    public double BaseCellSize { get; init; } = 0.1;
    public int SurfaceRefinementMin { get; init; } = 2;
    public int SurfaceRefinementMax { get; init; } = 3;
    public int FeatureRefinementLevel { get; init; } = 3;
    public int BoundaryLayerCount { get; init; } = 3;
    public double LayerExpansionRatio { get; init; } = 1.2;
    public double FinalLayerThickness { get; init; } = 0.3;
    public int MaxGlobalCells { get; init; } = 4_000_000;
    public double UpstreamLengths { get; init; } = 3;
    public double DownstreamLengths { get; init; } = 8;
    public double SideLengths { get; init; } = 4;
    public int EndTime { get; init; } = 1000;
    public int WriteInterval { get; init; } = 100;
    public int ProcessCount { get; init; } = 4;
    public string? FluidPointText { get; init; }
    public bool CalculateResiduals { get; init; } = true;
    public bool CalculateForces { get; init; } = true;
    public bool CalculateForceCoefficients { get; init; } = true;
    public bool CalculateWallShearStress { get; init; } = true;
    public bool CalculateYPlus { get; init; }
    public bool CalculateQCriterion { get; init; } = true;
    public bool CalculateVorticity { get; init; } = true;
    public bool CalculateTurbulenceIntensity { get; init; }
    public bool CalculateFieldAverage { get; init; }
    public string ForcePatches { get; init; } = "model.*";
    public double Density { get; init; } = 1.225;
    public double ReferenceArea { get; init; } = 1;
    public double ReferenceLength { get; init; } = 1;
    public string CentreOfRotationText { get; init; } = "0 0 0";
    public string DragDirectionText { get; init; } = "1 0 0";
    public string LiftDirectionText { get; init; } = "0 0 1";
    public string PitchAxisText { get; init; } = "0 1 0";
    public string AveragedFields { get; init; } = "U p";
    public string CustomFunctionObjects { get; init; } = "";

    public CadProjectSettings ToProjectSettings(
        string cadFilePath, string outputRootPath, string projectName) => new()
    {
        CadFilePath = cadFilePath,
        OutputRootPath = outputRootPath,
        ProjectName = projectName,
        CadUnit = CadUnit,
        AnalysisType = AnalysisType,
        FlowAxis = FlowAxis,
        Turbulence = Turbulence,
        Velocity = Velocity,
        KinematicViscosity = KinematicViscosity,
        TurbulenceIntensityPercent = TurbulenceIntensityPercent,
        TurbulenceLengthScale = TurbulenceLengthScale,
        CadSurfaceSize = CadSurfaceSize,
        BaseCellSize = BaseCellSize,
        SurfaceRefinementMin = SurfaceRefinementMin,
        SurfaceRefinementMax = SurfaceRefinementMax,
        FeatureRefinementLevel = FeatureRefinementLevel,
        BoundaryLayerCount = BoundaryLayerCount,
        LayerExpansionRatio = LayerExpansionRatio,
        FinalLayerThickness = FinalLayerThickness,
        MaxGlobalCells = MaxGlobalCells,
        UpstreamLengths = UpstreamLengths,
        DownstreamLengths = DownstreamLengths,
        SideLengths = SideLengths,
        EndTime = EndTime,
        WriteInterval = WriteInterval,
        ProcessCount = ProcessCount,
        FluidPointText = FluidPointText,
        CalculateResiduals = CalculateResiduals,
        CalculateForces = CalculateForces,
        CalculateForceCoefficients = CalculateForceCoefficients,
        CalculateWallShearStress = CalculateWallShearStress,
        CalculateYPlus = CalculateYPlus,
        CalculateQCriterion = CalculateQCriterion,
        CalculateVorticity = CalculateVorticity,
        CalculateTurbulenceIntensity = CalculateTurbulenceIntensity,
        CalculateFieldAverage = CalculateFieldAverage,
        ForcePatches = ForcePatches,
        Density = Density,
        ReferenceArea = ReferenceArea,
        ReferenceLength = ReferenceLength,
        CentreOfRotationText = CentreOfRotationText,
        DragDirectionText = DragDirectionText,
        LiftDirectionText = LiftDirectionText,
        PitchAxisText = PitchAxisText,
        AveragedFields = AveragedFields,
        CustomFunctionObjects = CustomFunctionObjects
    };
}

public readonly record struct Point3(double X, double Y, double Z)
{
    public override string ToString() =>
        $"({X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} " +
        $"{Y.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} " +
        $"{Z.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)})";
}

public readonly record struct GeometryBounds(Point3 Min, Point3 Max)
{
    public double XLength => Max.X - Min.X;
    public double YLength => Max.Y - Min.Y;
    public double ZLength => Max.Z - Min.Z;
    public double CharacteristicLength => Math.Max(XLength, Math.Max(YLength, ZLength));
}

public sealed class CadGenerationResult
{
    public required string CasePath { get; init; }
    public required GeometryBounds GeometryBounds { get; init; }
    public required GeometryBounds DomainBounds { get; init; }
    public required long BaseCellCount { get; init; }
    public required string ConversionOutput { get; init; }
}

public readonly record struct PreviewTriangle(Point3 A, Point3 B, Point3 C);

public sealed class CadPreviewData
{
    public required IReadOnlyList<PreviewTriangle> Triangles { get; init; }
    public required GeometryBounds Bounds { get; init; }
    public required int OriginalTriangleCount { get; init; }
    public required bool WasDisplayReduced { get; init; }
    public required string ConversionOutput { get; init; }
}

public sealed class AppSettings : INotifyPropertyChanged
{
    private RuntimeBackend _backend = RuntimeBackend.Wsl;
    private string _wslDistribution = "Ubuntu-24.04";
    private string _openFoamBashrc = "/opt/openfoam14/etc/bashrc";
    private string _dockerImage = "openfoam/openfoam14-paraview510";
    private string _paraViewPath = @"C:\Program Files\ParaView 6.1.0\bin\paraview.exe";
    private int _processCount = Math.Max(2, Environment.ProcessorCount / 2);
    private string _lastCadDirectory = "";
    private string _lastOutputRoot = "";

    public RuntimeBackend Backend { get => _backend; set => Set(ref _backend, value); }
    public string WslDistribution { get => _wslDistribution; set => Set(ref _wslDistribution, value); }
    public string OpenFoamBashrc { get => _openFoamBashrc; set => Set(ref _openFoamBashrc, value); }
    public string DockerImage { get => _dockerImage; set => Set(ref _dockerImage, value); }
    public string ParaViewPath { get => _paraViewPath; set => Set(ref _paraViewPath, value); }
    public int ProcessCount { get => _processCount; set => Set(ref _processCount, Math.Max(1, value)); }
    public string LastCadDirectory { get => _lastCadDirectory; set => Set(ref _lastCadDirectory, value); }
    public string LastOutputRoot { get => _lastOutputRoot; set => Set(ref _lastOutputRoot, value); }

    [JsonIgnore]
    public bool IsWsl => Backend == RuntimeBackend.Wsl;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(Backend))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWsl)));
    }
}

public sealed class PipelineStep : INotifyPropertyChanged
{
    private bool _isEnabled;
    private string _status = "대기";

    public required string Title { get; init; }
    public required string Command { get; init; }
    public required string Description { get; init; }
    public bool IsEnabled { get => _isEnabled; set { _isEnabled = value; Changed(); } }
    public string Status { get => _status; set { _status = value; Changed(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ResidualSample
{
    public required string Field { get; init; }
    public required double Initial { get; init; }
    public required double Final { get; init; }
    public required int Iterations { get; init; }
    public required int Sequence { get; init; }
}

public sealed class ResidualSummary : INotifyPropertyChanged
{
    private double _initial;
    private double _final;
    private int _iterations;
    private int _samples;

    public required string Field { get; init; }
    public double Initial { get => _initial; set { _initial = value; Changed(); } }
    public double Final { get => _final; set { _final = value; Changed(); } }
    public int Iterations { get => _iterations; set { _iterations = value; Changed(); } }
    public int Samples { get => _samples; set { _samples = value; Changed(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RuntimeProbe
{
    public bool IsAvailable { get; init; }
    public string Version { get; init; } = "감지 안 됨";
    public string Details { get; init; } = "";
}

public sealed class CaseInfo
{
    public string Path { get; init; } = "";
    public bool HasZero { get; init; }
    public bool HasConstant { get; init; }
    public bool HasSystem { get; init; }
    public string Application { get; init; } = "";
    public string SolverModule { get; init; } = "";
    public bool IsValid => HasZero && HasConstant && HasSystem;
}
