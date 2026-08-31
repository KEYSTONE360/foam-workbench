using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FoamWorkbench.Services;

public sealed class PorousCaseGenerator
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public PorousGenerationResult Generate(PorousCaseSettings settings)
    {
        var validation = PorousPhysics.Validate(settings);
        if (!validation.IsValid)
            throw new InvalidOperationException(BuildMissingPropertiesMessage(validation));
        if (string.IsNullOrWhiteSpace(settings.OutputRootPath))
            throw new ArgumentException("Porous Media case output folder is required.");
        if (string.IsNullOrWhiteSpace(settings.ProjectName))
            throw new ArgumentException("Porous Media project name is required.");

        var projectName = SanitizeProjectName(settings.ProjectName);
        var casePath = Path.Combine(Path.GetFullPath(settings.OutputRootPath), projectName);
        if (Directory.Exists(casePath) && Directory.EnumerateFileSystemEntries(casePath).Any())
            throw new IOException($"대상 폴더가 비어 있지 않습니다: {casePath}");

        var zero = Path.Combine(casePath, "0");
        var constant = Path.Combine(casePath, "constant");
        var system = Path.Combine(casePath, "system");
        Directory.CreateDirectory(zero);
        Directory.CreateDirectory(constant);
        Directory.CreateDirectory(system);

        var mesh = CalculateMesh(settings);
        var analytical = PorousPhysics.CalculateAnalytical(settings);
        Write(system, "blockMeshDict", CreateBlockMeshDictionary(settings, mesh));
        Write(system, "setFieldsDict", CreateSetFieldsDictionary(settings, mesh));
        Write(system, "controlDict", CreateControlDictionary(settings, mesh));
        Write(system, "fvSchemes", CreateFvSchemesDictionary(settings));
        Write(system, "fvSolution", CreateFvSolutionDictionary(settings));
        Write(system, "decomposeParDict", CreateDecomposeDictionary(settings.ProcessCount));
        Write(system, "sampleDict", CreateSampleDictionary(settings, mesh));
        Write(constant, "physicalProperties", CreatePhysicalPropertiesDictionary(settings));
        Write(constant, "momentumTransport", CreateMomentumTransportDictionary());
        Write(constant, "g", CreateGravityDictionary(settings));
        Write(constant, "fvModels", CreateFvModelsDictionary(settings));
        Write(zero, "U", CreateVelocityField(settings));
        Write(zero, "p", CreatePressureField(settings));
        Write(zero, "permeability", CreatePermeabilityField());
        Write(zero, "layerId", CreateLayerIdField());

        File.WriteAllText(Path.Combine(casePath, $"{projectName}.foam"), "", new UTF8Encoding(false));
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        File.WriteAllText(Path.Combine(casePath, "PorousWorkbenchProject.json"),
            JsonSerializer.Serialize(settings, jsonOptions), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(casePath, "PorousWorkbenchProject.txt"),
            CreateManifest(settings, mesh, analytical), new UTF8Encoding(false));

        return new PorousGenerationResult
        {
            CasePath = casePath,
            TotalCells = mesh.TotalCells,
            ZoneCellCounts = mesh.ZoneCells,
            Analytical = analytical
        };
    }

    public static string BuildMissingPropertiesMessage(PorousValidationResult validation)
    {
        var errors = validation.Errors;
        var text = new StringBuilder("Cannot start CFD simulation.\n\nMissing or invalid required physical properties:\n");
        foreach (var issue in errors) text.AppendLine($"\n{issue.Field}: {issue.Message}");
        return text.ToString().TrimEnd();
    }

    public static string CreateBlockMeshDictionary(PorousCaseSettings settings) =>
        CreateBlockMeshDictionary(settings, CalculateMesh(settings));

    public static string CreateFvModelsDictionary(PorousCaseSettings settings)
    {
        var text = new StringBuilder(FoamHeader("dictionary", "constant", "fvModels"));
        foreach (var layer in settings.Layers)
        {
            var (kx, ky, kz) = PermeabilityComponents(layer);
            var dx = PorousPhysics.PermeabilityToDarcyResistance(kx);
            var dy = PorousPhysics.PermeabilityToDarcyResistance(ky);
            var dz = PorousPhysics.PermeabilityToDarcyResistance(kz);
            var f = layer.ForchheimerCoefficient;
            text.AppendLine($$"""

porosity_{{layer.Name}}
{
    type            porosityForce;

    porosityForce
    {
        cellZone        {{layer.Name}};
        type            DarcyForchheimer;
        d               d [0 -2 0 0 0 0 0] ({{F(dx)}} {{F(dy)}} {{F(dz)}});
        f               f [0 -1 0 0 0 0 0] ({{F(f)}} {{F(f)}} {{F(f)}});

        coordinateSystem
        {
            type cartesian;
            origin (0 0 0);
            coordinateRotation
            {
                type axesRotation;
                e1 (1 0 0);
                e2 (0 1 0);
            }
        }
    }
}
""");
        }

        if (settings.GravityEnabled)
        {
            text.AppendLine("""

gravityForce
{
    type buoyancyForce;
}
""");
        }
        return text.ToString();
    }

    private static PorousMeshInfo CalculateMesh(PorousCaseSettings settings)
    {
        var width = PorousUnitConverter.MillimetresToMetres(settings.DomainWidthMm);
        var target = PorousUnitConverter.MillimetresToMetres(settings.TargetCellSizeMm);
        var depth = target;
        var nx = Math.Max(2, (int)Math.Ceiling(width / target));
        var layers = new List<PorousMeshLayer>();
        var y = 0.0;
        // UI/preset order is TOP -> BOTTOM, while blockMesh coordinates increase BOTTOM -> TOP.
        // Build the physical blocks in reverse so layer 1 touches the top inlet.
        foreach (var layer in settings.Layers.Reverse())
        {
            var thickness = PorousUnitConverter.MillimetresToMetres(layer.Thickness!.Value);
            var ny = Math.Max(settings.MinimumCellsPerLayer, (int)Math.Ceiling(thickness / target));
            layers.Add(new PorousMeshLayer(layer, y, y + thickness, ny));
            y += thickness;
        }
        return new PorousMeshInfo(width, depth, nx, layers);
    }

    private static string CreateBlockMeshDictionary(PorousCaseSettings settings, PorousMeshInfo mesh)
    {
        var vertices = new StringBuilder();
        for (var i = 0; i <= mesh.Layers.Count; i++)
        {
            var y = i == mesh.Layers.Count ? mesh.TotalThickness : mesh.Layers[i].YMin;
            vertices.AppendLine($"    ({F(0)} {F(y)} {F(0)})");
            vertices.AppendLine($"    ({F(mesh.Width)} {F(y)} {F(0)})");
            vertices.AppendLine($"    ({F(0)} {F(y)} {F(mesh.Depth)})");
            vertices.AppendLine($"    ({F(mesh.Width)} {F(y)} {F(mesh.Depth)})");
        }

        var blocks = new StringBuilder();
        var leftFaces = new StringBuilder();
        var rightFaces = new StringBuilder();
        var frontFaces = new StringBuilder();
        var backFaces = new StringBuilder();
        for (var i = 0; i < mesh.Layers.Count; i++)
        {
            var a = i * 4;
            var b = (i + 1) * 4;
            var layer = mesh.Layers[i];
            blocks.AppendLine(
                $"    hex ({a} {a + 1} {b + 1} {b} {a + 2} {a + 3} {b + 3} {b + 2}) " +
                $"{layer.Layer.Name} ({mesh.Nx} {layer.Ny} 1) simpleGrading (1 1 1)");
            leftFaces.AppendLine($"            ({a} {a + 2} {b + 2} {b})");
            rightFaces.AppendLine($"            ({a + 1} {b + 1} {b + 3} {a + 3})");
            frontFaces.AppendLine($"            ({a} {b} {b + 1} {a + 1})");
            backFaces.AppendLine($"            ({a + 2} {a + 3} {b + 3} {b + 2})");
        }

        var top = mesh.Layers.Count * 4;
        return FoamHeader("dictionary", "system", "blockMeshDict") + $$"""

convertToMeters 1;

vertices
(
{{vertices}});

blocks
(
{{blocks}});

edges ();

boundary
(
    bottom
    {
        type patch;
        faces ((0 1 3 2));
    }
    top
    {
        type patch;
        faces (({{top}} {{top + 2}} {{top + 3}} {{top + 1}}));
    }
    leftWall
    {
        type wall;
        faces
        (
{{leftFaces}}        );
    }
    rightWall
    {
        type wall;
        faces
        (
{{rightFaces}}        );
    }
    front
    {
        type empty;
        faces
        (
{{frontFaces}}        );
    }
    back
    {
        type empty;
        faces
        (
{{backFaces}}        );
    }
);

mergePatchPairs ();
""";
    }

    private static string CreateSetFieldsDictionary(PorousCaseSettings settings, PorousMeshInfo mesh)
    {
        var regions = new StringBuilder();
        foreach (var item in mesh.Layers)
        {
            var k = item.Layer.ThroughPermeability!.Value;
            regions.AppendLine($$"""
    boxToCell
    {
        box (0 {{F(item.YMin)}} 0) ({{F(mesh.Width)}} {{F(item.YMax)}} {{F(mesh.Depth)}});
        fieldValues
        (
            volScalarFieldValue permeability {{F(k)}}
            volScalarFieldValue layerId {{item.Layer.Id}}
        );
    }
""");
        }
        return FoamHeader("dictionary", "system", "setFieldsDict") + $$"""

defaultFieldValues
(
    volScalarFieldValue permeability 0
    volScalarFieldValue layerId 0
);

regions
(
{{regions}});
""";
    }

    private static string CreateControlDictionary(PorousCaseSettings settings, PorousMeshInfo mesh) =>
        FoamHeader("dictionary", "system", "controlDict") + $$"""

solver incompressibleFluid;

startFrom startTime;
startTime 0;
stopAt endTime;
endTime {{settings.EndTime}};
deltaT {{F(settings.SimulationType == PorousSimulationType.Steady ? 1 : settings.DeltaT)}};
writeControl {{(settings.SimulationType == PorousSimulationType.Steady ? "timeStep" : "runTime")}};
writeInterval {{settings.WriteInterval}};
purgeWrite 0;
writeFormat ascii;
writePrecision 12;
writeCompression off;
timeFormat general;
timePrecision 10;
runTimeModifiable true;

functions
{
{{CreateFunctionObjects(settings, mesh)}}
}
""";

    private static string CreateFvSchemesDictionary(PorousCaseSettings settings) =>
        FoamHeader("dictionary", "system", "fvSchemes") + $$"""

ddtSchemes
{
    default {{(settings.SimulationType == PorousSimulationType.Steady ? "steadyState" : "Euler")}};
}

gradSchemes
{
    default Gauss linear;
    grad(U) cellLimited Gauss linear 1;
}

divSchemes
{
    default none;
    div(phi,U) bounded Gauss upwind;
    div((nuEff*dev2(T(grad(U))))) Gauss linear;
}

laplacianSchemes
{
    default Gauss linear orthogonal;
}

interpolationSchemes
{
    default linear;
}

snGradSchemes
{
    default orthogonal;
}
""";

    private static string CreateFvSolutionDictionary(PorousCaseSettings settings)
    {
        var algorithm = settings.SimulationType == PorousSimulationType.Steady
            ? """
SIMPLE
{
    nNonOrthogonalCorrectors 0;
    consistent yes;
}
"""
            : """
PIMPLE
{
    momentumPredictor yes;
    nOuterCorrectors 1;
    nCorrectors 2;
    nNonOrthogonalCorrectors 0;
}
""";
        var finalSolvers = settings.SimulationType == PorousSimulationType.Transient
            ? """
    pFinal
    {
        $p;
        relTol 0;
    }
    UFinal
    {
        $U;
        relTol 0;
    }
"""
            : "";
        return FoamHeader("dictionary", "system", "fvSolution") + $$"""

solvers
{
    p
    {
        solver GAMG;
        smoother GaussSeidel;
        tolerance 1e-10;
        relTol 0.01;
    }
    Phi { $p; }
    U
    {
        solver smoothSolver;
        smoother GaussSeidel;
        tolerance 1e-10;
        relTol 0.05;
        nSweeps 1;
    }
{{finalSolvers}}
}

{{algorithm}}
relaxationFactors
{
    fields { p 0.3; }
    equations { U 0.7; }
}

cache
{
    grad(U);
}
""";
    }

    private static string CreateDecomposeDictionary(int count) =>
        FoamHeader("dictionary", "system", "decomposeParDict") + $$"""

numberOfSubdomains {{Math.Max(1, count)}};
decomposer scotch;
""";

    private static string CreatePhysicalPropertiesDictionary(PorousCaseSettings settings) =>
        FoamHeader("dictionary", "constant", "physicalProperties") + $$"""

viscosityModel constant;
nu {{F(PorousPhysics.KinematicViscosity(settings.DynamicViscosity, settings.Density))}};
""";

    private static string CreateMomentumTransportDictionary() =>
        FoamHeader("dictionary", "constant", "momentumTransport") + """

simulationType laminar;
""";

    private static string CreateGravityDictionary(PorousCaseSettings settings) =>
        FoamHeader("uniformDimensionedVectorField", "constant", "g") + $$"""

dimensions [0 1 -2 0 0 0 0];
value ({{F(settings.GravityEnabled ? settings.GravityX : 0)}} {{F(settings.GravityEnabled ? settings.GravityY : 0)}} {{F(settings.GravityEnabled ? settings.GravityZ : 0)}});
""";

    private static string CreateVelocityField(PorousCaseSettings settings)
    {
        var rainfall = PorousUnitConverter.MillimetresPerHourToMetresPerSecond(settings.RainfallMmPerHour);
        var initial = settings.FlowMode == PorousFlowMode.RainfallFlux
            ? $"(0 {F(-rainfall)} 0)"
            : "(0 0 0)";
        var top = settings.FlowMode == PorousFlowMode.RainfallFlux
            ? $$"""
    top
    {
        type fixedValue;
        value uniform (0 {{F(-rainfall)}} 0);
    }
"""
            : """
    top
    {
        type pressureInletOutletVelocity;
        value uniform (0 0 0);
    }
""";
        return FoamHeader("volVectorField", "0", "U") + $$"""

dimensions [0 1 -1 0 0 0 0];
internalField uniform {{initial}};

boundaryField
{
{{top}}    bottom
    {
        type pressureInletOutletVelocity;
        value uniform (0 0 0);
    }
    leftWall { type noSlip; }
    rightWall { type noSlip; }
    front { type empty; }
    back { type empty; }
}
""";
    }

    private static string CreatePressureField(PorousCaseSettings settings)
    {
        var gravityMagnitude = Math.Sqrt(
            settings.GravityX * settings.GravityX +
            settings.GravityY * settings.GravityY +
            settings.GravityZ * settings.GravityZ);
        if (gravityMagnitude <= 0) gravityMagnitude = 9.81;
        var headKinematic = gravityMagnitude * PorousUnitConverter.MillimetresToMetres(settings.WaterHeadMm);
        var top = settings.FlowMode switch
        {
            PorousFlowMode.RainfallFlux => "    top { type zeroGradient; }",
            PorousFlowMode.GravityDrainage => "    top { type fixedValue; value uniform 0; }",
            _ => $"    top {{ type fixedValue; value uniform {F(headKinematic)}; }}"
        };
        return FoamHeader("volScalarField", "0", "p") + $$"""

dimensions [0 2 -2 0 0 0 0];
internalField uniform 0;

boundaryField
{
{{top}}
    bottom { type fixedValue; value uniform 0; }
    leftWall { type zeroGradient; }
    rightWall { type zeroGradient; }
    front { type empty; }
    back { type empty; }
}
""";
    }

    private static string CreatePermeabilityField() =>
        FoamHeader("volScalarField", "0", "permeability") + """

dimensions [0 2 0 0 0 0 0];
internalField uniform 0;

boundaryField
{
    top { type zeroGradient; }
    bottom { type zeroGradient; }
    leftWall { type zeroGradient; }
    rightWall { type zeroGradient; }
    front { type empty; }
    back { type empty; }
}
""";

    private static string CreateLayerIdField() =>
        FoamHeader("volScalarField", "0", "layerId") + """

dimensions [0 0 0 0 0 0 0];
internalField uniform 0;

boundaryField
{
    top { type zeroGradient; }
    bottom { type zeroGradient; }
    leftWall { type zeroGradient; }
    rightWall { type zeroGradient; }
    front { type empty; }
    back { type empty; }
}
""";

    private static string CreateFunctionObjects(PorousCaseSettings settings, PorousMeshInfo mesh)
    {
        var residualWriteInterval = settings.SimulationType == PorousSimulationType.Transient
            ? Math.Max(1, (int)Math.Round(0.1 / settings.DeltaT))
            : 1;
        var text = new StringBuilder($$"""
    residuals
    {
        type residuals;
        libs ("libutilityFunctionObjects.so");
        fields (U p);
        writeControl timeStep;
        writeInterval {{residualWriteInterval}};
    }

    inletFlow
    {
        type surfaceFieldValue;
        libs ("libfieldFunctionObjects.so");
        writeControl writeTime;
        writeFields false;
        patch top;
        operation sum;
        fields (phi);
    }

    outletFlow
    {
        $inletFlow;
        patch bottom;
    }

    inletVelocity
    {
        type surfaceFieldValue;
        libs ("libfieldFunctionObjects.so");
        writeControl writeTime;
        writeFields false;
        patch top;
        operation areaAverage;
        fields (U);
    }

    outletVelocity
    {
        $inletVelocity;
        patch bottom;
    }

    inletPressure
    {
        type surfaceFieldValue;
        libs ("libfieldFunctionObjects.so");
        writeControl writeTime;
        writeFields false;
        patch top;
        operation areaAverage;
        fields (p);
    }

    outletPressure
    {
        $inletPressure;
        patch bottom;
    }
""");
        foreach (var layer in settings.Layers)
        {
            text.AppendLine($$"""

    average_{{layer.Name}}
    {
        type volFieldValue;
        libs ("libfieldFunctionObjects.so");
        writeControl writeTime;
        writeFields false;
        cellZone {{layer.Name}};
        operation volAverage;
        fields (p U);
    }
""");
        }
        text.AppendLine(CreateLayerInterfacePressureFunctions(settings, mesh));
        text.AppendLine(CreateCenterlineFunction(mesh));
        return text.ToString();
    }

    private static string CreateLayerInterfacePressureFunctions(
        PorousCaseSettings settings,
        PorousMeshInfo mesh)
    {
        var text = new StringBuilder();
        foreach (var layer in settings.Layers)
        {
            var physical = mesh.Layers.First(item => item.Layer.Name == layer.Name);
            var inset = Math.Max((physical.YMax - physical.YMin) * 1e-7, 1e-12);
            text.AppendLine(CreatePressurePlane($"pressureIn_{layer.Name}", physical.YMax - inset, mesh));
            text.AppendLine(CreatePressurePlane($"pressureOut_{layer.Name}", physical.YMin + inset, mesh));
        }
        return text.ToString();
    }

    private static string CreatePressurePlane(string name, double y, PorousMeshInfo mesh) => $$"""

    {{name}}
    {
        type surfaceFieldValue;
        libs ("libfieldFunctionObjects.so");
        writeControl writeTime;
        writeFields false;
        sampledSurface
        {
            type cutPlane;
            point ({{F(mesh.Width / 2)}} {{F(y)}} {{F(mesh.Depth / 2)}});
            normal (0 1 0);
            interpolate yes;
        }
        operation areaAverage;
        fields (p);
    }
""";

    private static string CreateSampleDictionary(PorousCaseSettings settings, PorousMeshInfo mesh) =>
        FoamHeader("dictionary", "system", "sampleDict") + CreateCenterlineFunction(mesh);

    private static string CreateCenterlineFunction(PorousMeshInfo mesh) => $$"""

centerline
{
    type sets;
    libs ("libsampling.so");
    writeControl writeTime;
    setFormat raw;
    interpolationScheme cellPoint;
    fields (p U);
    sets
    (
        centerline
        {
                type lineUniform;
            axis distance;
            start ({{F(mesh.Width / 2)}} {{F(mesh.TotalThickness * 1e-7)}} {{F(mesh.Depth / 2)}});
            end ({{F(mesh.Width / 2)}} {{F(mesh.TotalThickness * (1 - 1e-7))}} {{F(mesh.Depth / 2)}});
            nPoints 201;
        }
    );
}
""";

    private static string CreateManifest(
        PorousCaseSettings settings,
        PorousMeshInfo mesh,
        DarcyAnalysisResult analytical)
    {
        var layers = string.Join(Environment.NewLine, analytical.Layers.Select(layer =>
            $"{layer.LayerId}. {layer.DisplayName} | zone={layer.ZoneName} | L={F(layer.ThicknessMetres)} m | " +
            $"k={F(layer.ThroughPermeability)} m2 | d={F(1 / layer.ThroughPermeability)} m-2 | " +
            $"resistanceFraction={F(layer.ResistanceFraction * 100)} %"));
        return $$"""
Foam Workbench Porous Media Project
Generated: {{DateTimeOffset.Now:O}}
Preset: {{settings.PresetName}} ({{settings.PresetId}})
Preset source: {{settings.PresetSourceReference}}
Engine target: OpenFOAM Foundation 14 / foamRun / incompressibleFluid
Porous model: constant/fvModels porosityForce + DarcyForchheimer
Gravity model: constant/fvModels buoyancyForce (actual momentum source)
Pressure variable: kinematic p [m2/s2]; UI pressure results are multiplied by density
Flow mode: {{settings.FlowMode}}
Simulation type: {{settings.SimulationType}}
Domain width: {{F(mesh.Width)}} m
2D slice depth: {{F(mesh.Depth)}} m
Total thickness: {{F(mesh.TotalThickness)}} m
Cells: {{mesh.TotalCells}}
Water density: {{F(settings.Density)}} kg/m3
Dynamic viscosity: {{F(settings.DynamicViscosity)}} Pa.s
Kinematic viscosity: {{F(PorousPhysics.KinematicViscosity(settings.DynamicViscosity, settings.Density))}} m2/s
Gravity: ({{F(settings.GravityX)}} {{F(settings.GravityY)}} {{F(settings.GravityZ)}}) m/s2
Equivalent intrinsic permeability: {{F(analytical.EquivalentPermeability)}} m2
Hydraulic conductivity: {{F(analytical.HydraulicConductivity)}} m/s
Minimum hydraulic conductivity: {{(settings.MinimumHydraulicConductivity is null ? "not specified" : F(settings.MinimumHydraulicConductivity.Value) + " m/s")}}
CFD/analytical tolerance: {{(settings.CfdAnalyticalTolerancePercent is null ? "not specified" : F(settings.CfdAnalyticalTolerancePercent.Value) + " %")}}
Individual-zone bottleneck: {{analytical.Bottleneck.DisplayName}} ({{F(analytical.Bottleneck.ResistanceFraction * 100)}} %)
Design-stage bottleneck: Group {{analytical.BottleneckGroup.GroupId}} / {{analytical.BottleneckGroup.DisplayName}} ({{F(analytical.BottleneckGroup.ResistanceFraction * 100)}} %)

Layers
{{layers}}

Notes
- permeability is bulk intrinsic permeability, not pore size or particle size.
- permeability and layerId are visualization-only input fields; they are not solver-derived results.
- nominal residence time is layer thickness / volume-weighted through-flow velocity, not particle-tracking RTD.
- the out-of-plane depth equals one target cell size; reported volumetric flow belongs to this 2D slice.
""";
    }

    private static (double x, double y, double z) PermeabilityComponents(PorousLayer layer)
    {
        if (layer.PermeabilityType == PorousPermeabilityType.Isotropic)
        {
            var k = layer.Permeability!.Value;
            return (k, k, k);
        }
        return (layer.PermeabilityX!.Value, layer.PermeabilityY!.Value, layer.PermeabilityZ!.Value);
    }

    private static string SanitizeProjectName(string value)
    {
        var invalid = new string(Path.GetInvalidFileNameChars());
        var sanitized = Regex.Replace(value.Trim(), $"[{Regex.Escape(invalid)}]", "_");
        return string.IsNullOrWhiteSpace(sanitized) ? "TreeShieldPorous" : sanitized;
    }

    private static void Write(string directory, string fileName, string content) =>
        File.WriteAllText(Path.Combine(directory, fileName), content.Replace("\r\n", "\n"), new UTF8Encoding(false));

    private static string F(double value) => value.ToString("G17", Inv);

    private static string FoamHeader(string @class, string location, string objectName) => $$"""
/*--------------------------------*- C++ -*----------------------------------*\
  =========                 |
  \\      /  F ield         | OpenFOAM: The Open Source CFD Toolbox
   \\    /   O peration     | Generated by Foam Workbench
    \\  /    A nd           | Engine: OpenFOAM Foundation v14
     \\/     M anipulation  |
\*---------------------------------------------------------------------------*/
FoamFile
{
    format ascii;
    class {{@class}};
    location "{{location}}";
    object {{objectName}};
}
// * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * //
""";

    private sealed record PorousMeshLayer(PorousLayer Layer, double YMin, double YMax, int Ny);

    private sealed class PorousMeshInfo(
        double width,
        double depth,
        int nx,
        IReadOnlyList<PorousMeshLayer> layers)
    {
        public double Width { get; } = width;
        public double Depth { get; } = depth;
        public int Nx { get; } = nx;
        public IReadOnlyList<PorousMeshLayer> Layers { get; } = layers;
        public double TotalThickness => Layers.Count == 0 ? 0 : Layers[^1].YMax;
        public int TotalCells => Layers.Sum(layer => Nx * layer.Ny);
        public IReadOnlyDictionary<string, int> ZoneCells =>
            Layers.ToDictionary(layer => layer.Layer.Name, layer => Nx * layer.Ny, StringComparer.Ordinal);
    }
}
