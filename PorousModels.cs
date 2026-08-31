using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FoamWorkbench;

public enum WorkbenchCaseType
{
    ExternalAerodynamics,
    PorousMedia
}

public enum PorousMaterialCategory
{
    FiberMembrane,
    GranularFill
}

public enum PorousPermeabilityType
{
    Isotropic,
    Anisotropic
}

public enum PorousParameterSource
{
    Undefined,
    Experimental,
    Literature,
    Estimated,
    UserDefined
}

public enum PorousFlowMode
{
    RainfallFlux,
    GravityDrainage,
    WaterHead
}

public enum PorousSimulationType
{
    Steady,
    Transient
}

public enum PorousMeshPreset
{
    Coarse,
    Medium,
    Fine,
    Custom
}

public sealed record PorousVisualMetadata(
    string ColorHex,
    string Texture,
    string Description);

public sealed record PorousBuiltInPreset(
    string Id,
    string DisplayName,
    string Description)
{
    public override string ToString() => DisplayName;
}

public sealed class PorousLayer : INotifyPropertyChanged
{
    private int _id;
    private string _designGroup = "";
    private string _name = "layer";
    private string _displayNameKo = "";
    private string _displayNameEn = "";
    private PorousMaterialCategory _category;
    private string _materialType = "Thin membrane";
    private double? _thickness;
    private string _thicknessUnit = "mm";
    private PorousPermeabilityType _permeabilityType = PorousPermeabilityType.Isotropic;
    private double? _permeability;
    private double? _permeabilityX;
    private double? _permeabilityY;
    private double? _permeabilityZ;
    private double _forchheimerCoefficient;
    private double? _porosity;
    private double? _poreSizeMin;
    private double? _poreSizeMax;
    private double? _particleSize;
    private PorousParameterSource _parameterSource = PorousParameterSource.Undefined;
    private string _parameterSourceReference = "";
    private PorousVisualMetadata _visualMetadata = new("#BFC7D5", "", "");

    public int Id { get => _id; set => Set(ref _id, value); }
    public string DesignGroup { get => _designGroup; set => Set(ref _designGroup, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string DisplayNameKo { get => _displayNameKo; set => Set(ref _displayNameKo, value); }
    public string DisplayNameEn { get => _displayNameEn; set => Set(ref _displayNameEn, value); }
    public PorousMaterialCategory Category { get => _category; set => Set(ref _category, value); }
    public string MaterialType { get => _materialType; set => Set(ref _materialType, value); }
    public double? Thickness { get => _thickness; set { if (Set(ref _thickness, value)) NotifyDerived(); } }
    public string ThicknessUnit { get => _thicknessUnit; set => Set(ref _thicknessUnit, value); }
    public PorousPermeabilityType PermeabilityType { get => _permeabilityType; set { if (Set(ref _permeabilityType, value)) NotifyDerived(); } }
    public double? Permeability { get => _permeability; set { if (Set(ref _permeability, value)) NotifyDerived(); } }
    public double? PermeabilityX { get => _permeabilityX; set { if (Set(ref _permeabilityX, value)) NotifyDerived(); } }
    public double? PermeabilityY { get => _permeabilityY; set { if (Set(ref _permeabilityY, value)) NotifyDerived(); } }
    public double? PermeabilityZ { get => _permeabilityZ; set { if (Set(ref _permeabilityZ, value)) NotifyDerived(); } }
    public double ForchheimerCoefficient { get => _forchheimerCoefficient; set => Set(ref _forchheimerCoefficient, value); }
    public double? Porosity { get => _porosity; set => Set(ref _porosity, value); }
    public double? PoreSizeMin { get => _poreSizeMin; set => Set(ref _poreSizeMin, value); }
    public double? PoreSizeMax { get => _poreSizeMax; set => Set(ref _poreSizeMax, value); }
    public double? ParticleSize { get => _particleSize; set => Set(ref _particleSize, value); }
    public PorousParameterSource ParameterSource { get => _parameterSource; set => Set(ref _parameterSource, value); }
    public string ParameterSourceReference { get => _parameterSourceReference; set => Set(ref _parameterSourceReference, value); }
    public PorousVisualMetadata VisualMetadata { get => _visualMetadata; set => Set(ref _visualMetadata, value); }

    public double? ThroughPermeability => PermeabilityType == PorousPermeabilityType.Isotropic
        ? Permeability
        : PermeabilityY;

    public double? DarcyResistance => ThroughPermeability is > 0 and var k ? 1.0 / k : null;

    public string InputState => Thickness is null || ThroughPermeability is null
        ? "INPUT REQUIRED"
        : "READY";

    public PorousLayer Clone() => new()
    {
        Id = Id,
        DesignGroup = DesignGroup,
        Name = Name,
        DisplayNameKo = DisplayNameKo,
        DisplayNameEn = DisplayNameEn,
        Category = Category,
        MaterialType = MaterialType,
        Thickness = Thickness,
        ThicknessUnit = ThicknessUnit,
        PermeabilityType = PermeabilityType,
        Permeability = Permeability,
        PermeabilityX = PermeabilityX,
        PermeabilityY = PermeabilityY,
        PermeabilityZ = PermeabilityZ,
        ForchheimerCoefficient = ForchheimerCoefficient,
        Porosity = Porosity,
        PoreSizeMin = PoreSizeMin,
        PoreSizeMax = PoreSizeMax,
        ParticleSize = ParticleSize,
        ParameterSource = ParameterSource,
        ParameterSourceReference = ParameterSourceReference,
        VisualMetadata = VisualMetadata
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void NotifyDerived()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThroughPermeability)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DarcyResistance)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InputState)));
    }
}

public sealed class PorousCaseSettings
{
    public string PresetId { get; init; } = "custom";
    public string PresetName { get; init; } = "Custom";
    public string PresetSourceReference { get; init; } = "";
    public double? MinimumHydraulicConductivity { get; init; }
    public double? CfdAnalyticalTolerancePercent { get; init; }
    public string OutputRootPath { get; init; } = "";
    public string ProjectName { get; init; } = "TreeShieldPorous";
    public double DomainWidthMm { get; init; } = 80;
    public IReadOnlyList<PorousLayer> Layers { get; init; } = [];
    public double Density { get; init; } = 998.2;
    public double DynamicViscosity { get; init; } = 1.003e-3;
    public bool GravityEnabled { get; init; } = true;
    public double GravityX { get; init; }
    public double GravityY { get; init; } = -9.81;
    public double GravityZ { get; init; }
    public PorousFlowMode FlowMode { get; init; } = PorousFlowMode.RainfallFlux;
    public double RainfallMmPerHour { get; init; } = 20;
    public double WaterHeadMm { get; init; } = 50;
    public PorousSimulationType SimulationType { get; init; } = PorousSimulationType.Steady;
    public PorousMeshPreset MeshPreset { get; init; } = PorousMeshPreset.Medium;
    public double TargetCellSizeMm { get; init; } = 0.25;
    public int MinimumCellsPerLayer { get; init; } = 4;
    public int EndTime { get; init; } = 400;
    public int WriteInterval { get; init; } = 40;
    public double DeltaT { get; init; } = 0.001;
    public int ProcessCount { get; init; } = 1;

    public PorousCaseSettings CloneWith(
        IReadOnlyList<PorousLayer>? layers = null,
        string? outputRootPath = null,
        string? projectName = null,
        int? endTime = null,
        int? writeInterval = null,
        double? deltaT = null) => new()
    {
        PresetId = PresetId,
        PresetName = PresetName,
        PresetSourceReference = PresetSourceReference,
        MinimumHydraulicConductivity = MinimumHydraulicConductivity,
        CfdAnalyticalTolerancePercent = CfdAnalyticalTolerancePercent,
        OutputRootPath = outputRootPath ?? OutputRootPath,
        ProjectName = projectName ?? ProjectName,
        DomainWidthMm = DomainWidthMm,
        Layers = layers ?? Layers.Select(layer => layer.Clone()).ToArray(),
        Density = Density,
        DynamicViscosity = DynamicViscosity,
        GravityEnabled = GravityEnabled,
        GravityX = GravityX,
        GravityY = GravityY,
        GravityZ = GravityZ,
        FlowMode = FlowMode,
        RainfallMmPerHour = RainfallMmPerHour,
        WaterHeadMm = WaterHeadMm,
        SimulationType = SimulationType,
        MeshPreset = MeshPreset,
        TargetCellSizeMm = TargetCellSizeMm,
        MinimumCellsPerLayer = MinimumCellsPerLayer,
        EndTime = endTime ?? EndTime,
        WriteInterval = writeInterval ?? WriteInterval,
        DeltaT = deltaT ?? DeltaT,
        ProcessCount = ProcessCount
    };
}

public sealed record PorousValidationIssue(string Field, string Message, bool IsError);

public sealed class PorousValidationResult
{
    public required IReadOnlyList<PorousValidationIssue> Issues { get; init; }
    public IReadOnlyList<PorousValidationIssue> Errors => Issues.Where(issue => issue.IsError).ToArray();
    public IReadOnlyList<PorousValidationIssue> Warnings => Issues.Where(issue => !issue.IsError).ToArray();
    public bool IsValid => Errors.Count == 0;
}

public sealed record LayerDarcyResult(
    int LayerId,
    string ZoneName,
    string DisplayName,
    double ThicknessMetres,
    double ThroughPermeability,
    double Resistance,
    double ResistanceFraction);

public sealed record LayerResistanceGroupResult(
    string GroupId,
    string DisplayName,
    double Resistance,
    double ResistanceFraction,
    IReadOnlyList<string> ZoneNames);

public sealed class DarcyAnalysisResult
{
    public required double TotalThicknessMetres { get; init; }
    public required double EquivalentPermeability { get; init; }
    public required double HydraulicConductivity { get; init; }
    public required double RequiredRainfallVelocity { get; init; }
    public required double SafetyFactor { get; init; }
    public required IReadOnlyList<LayerDarcyResult> Layers { get; init; }
    public required LayerDarcyResult Bottleneck { get; init; }
    public required IReadOnlyList<LayerResistanceGroupResult> Groups { get; init; }
    public required LayerResistanceGroupResult BottleneckGroup { get; init; }
}

public sealed class PorousGenerationResult
{
    public required string CasePath { get; init; }
    public required int TotalCells { get; init; }
    public required IReadOnlyDictionary<string, int> ZoneCellCounts { get; init; }
    public required DarcyAnalysisResult Analytical { get; init; }
}

public sealed record PorousFlowBalance(
    double InletFlowRate,
    double OutletFlowRate,
    double DifferencePercent,
    bool Pass);

public sealed record PorousLayerResult(
    int LayerId,
    string Name,
    double AveragePressurePa,
    double AverageInletPressurePa,
    double AverageOutletPressurePa,
    double PressureDropPa,
    double AverageVelocity,
    double AverageThroughVelocity,
    double NominalResidenceTime,
    double ResistanceFraction);

public enum PorousSimulationStatus
{
    NotConverged,
    Converged,
    Failed
}

public sealed class PorousResultSummary
{
    public string CasePath { get; init; } = "";
    public double InletFlowRate { get; init; } = double.NaN;
    public double OutletFlowRate { get; init; } = double.NaN;
    public double InletPressurePa { get; init; } = double.NaN;
    public double OutletPressurePa { get; init; } = double.NaN;
    public double PressureDropPa => InletPressurePa - OutletPressurePa;
    public double CfdEquivalentPermeability { get; init; } = double.NaN;
    public double CfdHydraulicConductivity { get; init; } = double.NaN;
    public double CfdAnalyticalDifferencePercent { get; init; } = double.NaN;
    public PorousSimulationStatus SimulationStatus { get; init; } = PorousSimulationStatus.NotConverged;
    public double FinalTime { get; init; } = double.NaN;
    public int ResultDirectoryCount { get; init; }
    public double FinalResidualMaximum { get; init; } = double.NaN;
    public double InletAverageVelocity { get; init; } = double.NaN;
    public double OutletAverageVelocity { get; init; } = double.NaN;
    public double ExpectedInletVelocity { get; init; } = double.NaN;
    public bool InletVelocityPreserved { get; init; } = true;
    public string PressureFieldName { get; init; } = "p";
    public string PressureUnitDescription { get; init; } = "m²/s² (kinematic); Workbench converts to Pa using density";
    public IReadOnlyList<string> SanityMessages { get; init; } = [];
    public PorousFlowBalance? FlowBalance { get; init; }
    public IReadOnlyList<PorousLayerResult> Layers { get; init; } = [];
    public string CenterlineCsvPath { get; init; } = "";
}

public sealed record PorousSweepRequest(
    int LayerIndex,
    double StartPermeability,
    double EndPermeability,
    int Steps,
    bool RunSolver);

public sealed record PorousSweepRow(
    int Step,
    string CasePath,
    string Parameter,
    double Permeability,
    double FlowRate,
    double PressureDrop,
    double AnalyticalKeff,
    double CfdKeff,
    double ErrorPercent,
    double BottleneckFraction);
