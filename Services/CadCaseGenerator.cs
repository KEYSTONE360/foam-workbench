using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace FoamWorkbench.Services;

public sealed class CadCaseGenerator(OpenFoamService openFoam)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public async Task<CadGenerationResult> GenerateAsync(
        CadProjectSettings settings,
        CancellationToken cancellationToken = default)
    {
        Validate(settings);

        var projectName = SanitizeProjectName(settings.ProjectName);
        var casePath = Path.Combine(Path.GetFullPath(settings.OutputRootPath), projectName);
        if (Directory.Exists(casePath) && Directory.EnumerateFileSystemEntries(casePath).Any())
            throw new IOException($"대상 폴더가 비어 있지 않습니다: {casePath}");

        var zero = Path.Combine(casePath, "0");
        var constant = Path.Combine(casePath, "constant");
        var geometryDirectory = Path.Combine(constant, "geometry");
        var system = Path.Combine(casePath, "system");
        Directory.CreateDirectory(zero);
        Directory.CreateDirectory(geometryDirectory);
        Directory.CreateDirectory(system);

        var extension = Path.GetExtension(settings.CadFilePath).ToLowerInvariant();
        var sourceName = $"source{extension}";
        File.Copy(settings.CadFilePath, Path.Combine(geometryDirectory, sourceName), overwrite: true);

        var conversionLog = new StringBuilder();
        if (extension == ".stl")
        {
            File.Copy(Path.Combine(geometryDirectory, sourceName),
                Path.Combine(geometryDirectory, "modelRaw.stl"), overwrite: true);
        }
        else
        {
            var size = F(settings.CadSurfaceSize);
            var command =
                $"gmsh 'constant/geometry/{sourceName}' -2 -format stl " +
                $"-o 'constant/geometry/modelRaw.stl' -clmin {size} -clmax {size} -setnumber Mesh.Binary 0";
            var result = await openFoam.RunCaseCommandAsync(casePath, command, cancellationToken);
            conversionLog.AppendLine(result.Output);
            if (result.ExitCode != 0 || !File.Exists(Path.Combine(geometryDirectory, "modelRaw.stl")))
                throw new InvalidOperationException(
                    "Gmsh/OpenCASCADE가 CAD 표면을 변환하지 못했습니다. CAD가 손상되었거나 지원하지 않는 형식일 수 있습니다.");
        }

        var scale = settings.CadUnit == CadLengthUnit.Millimetre ? 0.001 : 1.0;
        var scaleText = F(scale);
        var transform =
            $"surfaceTransformPoints \"scale=({scaleText} {scaleText} {scaleText})\" " +
            "'constant/geometry/modelRaw.stl' 'constant/geometry/model.stl'";
        var transformResult = await openFoam.RunCaseCommandAsync(casePath, transform, cancellationToken);
        conversionLog.AppendLine(transformResult.Output);
        if (transformResult.ExitCode != 0)
            throw new InvalidOperationException("OpenFOAM surfaceTransformPoints 단위 변환에 실패했습니다.");

        var surfacePath = Path.Combine(geometryDirectory, "model.stl");
        var geometry = StlBoundsReader.Read(surfacePath);
        if (geometry.CharacteristicLength <= 1e-12)
            throw new InvalidDataException("CAD 형상의 크기가 0에 가깝습니다. 길이 단위를 확인하세요.");

        var surfaceCheck = await openFoam.RunCaseCommandAsync(
            casePath, "surfaceCheck 'constant/geometry/model.stl'", cancellationToken);
        conversionLog.AppendLine(surfaceCheck.Output);
        if (surfaceCheck.ExitCode != 0)
            throw new InvalidOperationException("OpenFOAM surfaceCheck가 CAD 표면 오류를 발견했습니다.");

        var domain = settings.AnalysisType == CadAnalysisType.ExternalFlow
            ? BuildExternalDomain(geometry, settings)
            : Expand(geometry, Math.Max(settings.BaseCellSize, geometry.CharacteristicLength * 0.02));

        var nx = Math.Max(2, (int)Math.Ceiling(domain.XLength / settings.BaseCellSize));
        var ny = Math.Max(2, (int)Math.Ceiling(domain.YLength / settings.BaseCellSize));
        var nz = Math.Max(2, (int)Math.Ceiling(domain.ZLength / settings.BaseCellSize));
        var baseCells = checked((long)nx * ny * nz);

        WriteCase(settings, casePath, geometry, domain, nx, ny, nz);

        File.WriteAllText(Path.Combine(casePath, $"{projectName}.foam"), "", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(casePath, "FoamWorkbenchProject.txt"),
            BuildProjectManifest(settings, geometry, domain, baseCells), new UTF8Encoding(false));

        return new CadGenerationResult
        {
            CasePath = casePath,
            GeometryBounds = geometry,
            DomainBounds = domain,
            BaseCellCount = baseCells,
            ConversionOutput = conversionLog.ToString()
        };
    }

    private static void Validate(CadProjectSettings s)
    {
        if (!File.Exists(s.CadFilePath)) throw new FileNotFoundException("CAD 파일을 찾을 수 없습니다.", s.CadFilePath);
        var supported = new[] { ".step", ".stp", ".iges", ".igs", ".brep", ".stl" };
        if (!supported.Contains(Path.GetExtension(s.CadFilePath), StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException("STEP, STP, IGES, IGS, BREP, STL 형식만 지원합니다.");
        if (string.IsNullOrWhiteSpace(s.OutputRootPath)) throw new ArgumentException("결과 저장 폴더를 지정하세요.");
        if (string.IsNullOrWhiteSpace(s.ProjectName)) throw new ArgumentException("프로젝트 이름을 입력하세요.");
        if (s.Velocity <= 0 || s.KinematicViscosity <= 0 || s.BaseCellSize <= 0 || s.CadSurfaceSize <= 0)
            throw new ArgumentException("속도, 점성계수, CAD 표면 크기, 기본 셀 크기는 0보다 커야 합니다.");
        if (s.SurfaceRefinementMin < 0 || s.SurfaceRefinementMax < s.SurfaceRefinementMin)
            throw new ArgumentException("표면 최대 세분화 레벨은 최소 레벨 이상이어야 합니다.");
        if (s.BoundaryLayerCount < 0 || s.MaxGlobalCells < 1)
            throw new ArgumentException("경계층 수와 최대 셀 수를 확인하세요.");
        if (s.AnalysisType == CadAnalysisType.InternalFluidVolume)
            _ = ParsePoint(s.FluidPointText ?? "");
        _ = OpenFoamFunctionObjectBuilder.Build(s);
    }

    private static string SanitizeProjectName(string value)
    {
        var invalid = new string(Path.GetInvalidFileNameChars());
        var sanitized = Regex.Replace(value.Trim(), $"[{Regex.Escape(invalid)}]", "_");
        return string.IsNullOrWhiteSpace(sanitized) ? "NewCfdProject" : sanitized;
    }

    private static GeometryBounds BuildExternalDomain(GeometryBounds g, CadProjectSettings s)
    {
        var length = g.CharacteristicLength;
        var min = g.Min;
        var max = g.Max;
        var side = s.SideLengths * length;
        var upstream = s.UpstreamLengths * length;
        var downstream = s.DownstreamLengths * length;

        return s.FlowAxis switch
        {
            FlowAxis.PositiveX => new(
                new Point3(min.X - upstream, min.Y - side, min.Z - side),
                new Point3(max.X + downstream, max.Y + side, max.Z + side)),
            FlowAxis.NegativeX => new(
                new Point3(min.X - downstream, min.Y - side, min.Z - side),
                new Point3(max.X + upstream, max.Y + side, max.Z + side)),
            FlowAxis.PositiveY => new(
                new Point3(min.X - side, min.Y - upstream, min.Z - side),
                new Point3(max.X + side, max.Y + downstream, max.Z + side)),
            FlowAxis.NegativeY => new(
                new Point3(min.X - side, min.Y - downstream, min.Z - side),
                new Point3(max.X + side, max.Y + upstream, max.Z + side)),
            FlowAxis.PositiveZ => new(
                new Point3(min.X - side, min.Y - side, min.Z - upstream),
                new Point3(max.X + side, max.Y + side, max.Z + downstream)),
            _ => new(
                new Point3(min.X - side, min.Y - side, min.Z - downstream),
                new Point3(max.X + side, max.Y + side, max.Z + upstream))
        };
    }

    private static GeometryBounds Expand(GeometryBounds g, double amount) => new(
        new Point3(g.Min.X - amount, g.Min.Y - amount, g.Min.Z - amount),
        new Point3(g.Max.X + amount, g.Max.Y + amount, g.Max.Z + amount));

    private static void WriteCase(
        CadProjectSettings s,
        string casePath,
        GeometryBounds geometry,
        GeometryBounds domain,
        int nx,
        int ny,
        int nz)
    {
        var zero = Path.Combine(casePath, "0");
        var constant = Path.Combine(casePath, "constant");
        var system = Path.Combine(casePath, "system");

        Write(system, "blockMeshDict", BlockMeshDict(s, domain, nx, ny, nz));
        Write(system, "surfaceFeaturesDict", SurfaceFeaturesDict());
        Write(system, "snappyHexMeshDict", SnappyHexMeshDict(s, geometry, domain));
        Write(system, "meshQualityDict", MeshQualityDict());
        Write(system, "controlDict", ControlDict(s));
        Write(system, "FoamWorkbenchFunctions", OpenFoamFunctionObjectBuilder.Build(s));
        Write(system, "fvSchemes", FvSchemes(s));
        Write(system, "fvSolution", FvSolution(s));
        Write(system, "decomposeParDict", DecomposeParDict(s.ProcessCount));
        if (s.AnalysisType == CadAnalysisType.InternalFluidVolume)
            Write(system, "createPatchDict", CreatePatchDict(s, geometry));

        Write(constant, "physicalProperties", PhysicalProperties(s));
        Write(constant, "momentumTransport", MomentumTransport(s));

        Write(zero, "U", UField(s));
        Write(zero, "p", PField(s));
        if (s.Turbulence == TurbulenceChoice.KOmegaSst)
        {
            var (k, omega) = TurbulenceValues(s);
            Write(zero, "k", KField(s, k));
            Write(zero, "omega", OmegaField(s, omega));
            Write(zero, "nut", NutField(s));
        }
    }

    private static string BlockMeshDict(CadProjectSettings s, GeometryBounds d, int nx, int ny, int nz)
    {
        var min = d.Min;
        var max = d.Max;
        var faces = new Dictionary<string, string>
        {
            ["xMin"] = "(0 4 7 3)",
            ["xMax"] = "(1 2 6 5)",
            ["yMin"] = "(0 1 5 4)",
            ["yMax"] = "(3 7 6 2)",
            ["zMin"] = "(0 3 2 1)",
            ["zMax"] = "(4 5 6 7)"
        };
        var (inletName, outletName) = AxisPatchNames(s.FlowAxis);
        var inletFace = faces[inletName];
        var outletFace = faces[outletName];
        var otherFaces = string.Join("\n            ", faces
            .Where(pair => pair.Key != inletName && pair.Key != outletName)
            .Select(pair => pair.Value));

        var boundary = s.AnalysisType == CadAnalysisType.ExternalFlow
            ? $$"""
    inlet
    {
        type patch;
        faces ( {{inletFace}} );
    }
    outlet
    {
        type patch;
        faces ( {{outletFace}} );
    }
    farField
    {
        type patch;
        faces
        (
            {{otherFaces}}
        );
    }
"""
            : $$"""
    background
    {
        type patch;
        faces
        (
            {{string.Join("\n            ", faces.Values)}}
        );
    }
""";

        return FoamHeader("dictionary", "system", "blockMeshDict") + $$"""

vertices
(
    {{new Point3(min.X, min.Y, min.Z)}}
    {{new Point3(max.X, min.Y, min.Z)}}
    {{new Point3(max.X, max.Y, min.Z)}}
    {{new Point3(min.X, max.Y, min.Z)}}
    {{new Point3(min.X, min.Y, max.Z)}}
    {{new Point3(max.X, min.Y, max.Z)}}
    {{new Point3(max.X, max.Y, max.Z)}}
    {{new Point3(min.X, max.Y, max.Z)}}
);

blocks
(
    hex (0 1 2 3 4 5 6 7) ({{nx}} {{ny}} {{nz}}) simpleGrading (1 1 1)
);

edges ();

boundary
(
{{boundary}}
);

mergePatchPairs ();
""";
    }

    private static string SurfaceFeaturesDict() => FoamHeader("dictionary", "system", "surfaceFeaturesDict") + """

surfaces ("model.stl");

includedAngle 150;

subsetFeatures
{
    nonManifoldEdges no;
    openEdges yes;
}
""";

    private static string SnappyHexMeshDict(
        CadProjectSettings s,
        GeometryBounds geometry,
        GeometryBounds domain)
    {
        var keepPoint = s.AnalysisType == CadAnalysisType.InternalFluidVolume
            ? ParsePoint(s.FluidPointText ?? "")
            : ExternalKeepPoint(s.FlowAxis, domain);
        var near = Expand(geometry, geometry.CharacteristicLength * 1.5);
        var featureLevel = s.FeatureRefinementLevel;
        var addLayers = s.BoundaryLayerCount > 0 ? "true" : "false";

        return FoamHeader("dictionary", "system", "snappyHexMeshDict") + $$"""

castellatedMesh true;
snap true;
addLayers {{addLayers}};

geometry
{
    model
    {
        type triSurface;
        file "model.stl";
    }

    nearBody
    {
        type box;
        min {{near.Min}};
        max {{near.Max}};
    }
}

castellatedMeshControls
{
    maxLocalCells {{Math.Max(100000, s.MaxGlobalCells / Math.Max(1, s.ProcessCount))}};
    maxGlobalCells {{s.MaxGlobalCells}};
    minRefinementCells 10;
    maxLoadUnbalance 0.10;
    nCellsBetweenLevels 3;

    features
    (
        {
            file "model.eMesh";
            level {{featureLevel}};
        }
    );

    refinementSurfaces
    {
        model
        {
            level ({{s.SurfaceRefinementMin}} {{s.SurfaceRefinementMax}});
            patchInfo
            {
                type wall;
                inGroups (modelGroup);
            }
        }
    }

    resolveFeatureAngle 30;

    refinementRegions
    {
        nearBody
        {
            mode inside;
            level {{Math.Max(1, s.SurfaceRefinementMin - 1)}};
        }
    }

    insidePoint {{keepPoint}};
    allowFreeStandingZoneFaces true;
}

snapControls
{
    nSmoothPatch 3;
    tolerance 2.0;
    nSolveIter 30;
    nRelaxIter 5;
    nFeatureSnapIter 10;
    implicitFeatureSnap false;
    explicitFeatureSnap true;
    multiRegionFeatureSnap false;
}

addLayersControls
{
    relativeSizes true;

    layers
    {
        "model.*"
        {
            nSurfaceLayers {{s.BoundaryLayerCount}};
        }
    }

    expansionRatio {{F(s.LayerExpansionRatio)}};
    finalLayerThickness {{F(s.FinalLayerThickness)}};
    minThickness 0.1;
    nGrow 0;
    featureAngle 100;
    slipFeatureAngle 30;
    nRelaxIter 3;
    nSmoothSurfaceNormals 1;
    nSmoothNormals 3;
    nSmoothThickness 10;
    maxFaceThicknessRatio 0.5;
    maxThicknessToMedialRatio 0.3;
    minMedianAxisAngle 90;
    nBufferCellsNoExtrude 0;
    nLayerIter 50;
}

meshQualityControls
{
    #include "meshQualityDict"
}

writeFlags
(
    scalarLevels
    layerSets
    layerFields
);

mergeTolerance 1e-6;
""";
    }

    private static string MeshQualityDict() => FoamHeader("dictionary", "system", "meshQualityDict") + """

#includeEtc "caseDicts/mesh/generation/meshQualityDict"

minFaceWeight 0.02;
""";

    private static string ControlDict(CadProjectSettings s) =>
        FoamHeader("dictionary", "system", "controlDict") + $$"""

solver incompressibleFluid;

startFrom startTime;
startTime 0;
stopAt endTime;
endTime {{s.EndTime}};
deltaT 1;
writeControl timeStep;
writeInterval {{s.WriteInterval}};
purgeWrite 0;
writeFormat binary;
writePrecision 10;
writeCompression off;
timeFormat general;
timePrecision 8;
runTimeModifiable true;

functions
{
    #include "FoamWorkbenchFunctions"
}
""";

    private static string FvSchemes(CadProjectSettings s)
    {
        var turbulenceDiv = s.Turbulence == TurbulenceChoice.KOmegaSst
            ? "    div(phi,k)      bounded Gauss upwind;\n    div(phi,omega)  bounded Gauss upwind;\n"
            : "";
        return FoamHeader("dictionary", "system", "fvSchemes") + $$"""

ddtSchemes
{
    default steadyState;
}

gradSchemes
{
    default Gauss linear;
    grad(U) cellLimited Gauss linear 1;
}

divSchemes
{
    default none;
    div(phi,U) bounded Gauss linearUpwindV grad(U);
{{turbulenceDiv}}    div((nuEff*dev2(T(grad(U))))) Gauss linear;
}

laplacianSchemes
{
    default Gauss linear corrected;
}

interpolationSchemes
{
    default linear;
}

snGradSchemes
{
    default corrected;
}

wallDist
{
    method meshWave;
}
""";
    }

    private static string FvSolution(CadProjectSettings s)
    {
        var turbulenceSolvers = s.Turbulence == TurbulenceChoice.KOmegaSst
            ? """

    k
    {
        solver smoothSolver;
        smoother GaussSeidel;
        tolerance 1e-8;
        relTol 0.1;
        nSweeps 1;
    }

    omega
    {
        solver smoothSolver;
        smoother GaussSeidel;
        tolerance 1e-8;
        relTol 0.1;
        nSweeps 1;
    }
"""
            : "";
        var turbulenceRelaxation = s.Turbulence == TurbulenceChoice.KOmegaSst
            ? "        k 0.5;\n        omega 0.5;\n"
            : "";

        return FoamHeader("dictionary", "system", "fvSolution") + $$"""

solvers
{
    p
    {
        solver GAMG;
        smoother GaussSeidel;
        tolerance 1e-7;
        relTol 0.01;
    }

    Phi
    {
        $p;
    }

    U
    {
        solver smoothSolver;
        smoother GaussSeidel;
        tolerance 1e-8;
        relTol 0.1;
        nSweeps 1;
    }
{{turbulenceSolvers}}}

SIMPLE
{
    nNonOrthogonalCorrectors 0;
    consistent yes;
}

potentialFlow
{
    nNonOrthogonalCorrectors 10;
}

relaxationFactors
{
    equations
    {
        U 0.9;
{{turbulenceRelaxation}}    }
}

cache
{
    grad(U);
}
""";
    }

    private static string DecomposeParDict(int count) =>
        FoamHeader("dictionary", "system", "decomposeParDict") + $$"""

numberOfSubdomains {{Math.Max(1, count)}};
decomposer scotch;
""";

    private static string PhysicalProperties(CadProjectSettings s) =>
        FoamHeader("dictionary", "constant", "physicalProperties") + $$"""

viscosityModel constant;
nu {{F(s.KinematicViscosity)}};
""";

    private static string MomentumTransport(CadProjectSettings s) =>
        FoamHeader("dictionary", "constant", "momentumTransport") +
        (s.Turbulence == TurbulenceChoice.KOmegaSst
            ? """

simulationType RAS;

RAS
{
    model kOmegaSST;
    turbulence on;
}
"""
            : """

simulationType laminar;
""");

    private static string UField(CadProjectSettings s)
    {
        var velocity = FlowVector(s.FlowAxis, s.Velocity);
        var boundaries = s.AnalysisType == CadAnalysisType.ExternalFlow
            ? $$"""
    inlet
    {
        type fixedValue;
        value uniform {{velocity}};
    }
    outlet
    {
        type inletOutlet;
        inletValue uniform (0 0 0);
        value uniform {{velocity}};
    }
    farField
    {
        type freestream;
        freestreamValue uniform {{velocity}};
        value uniform {{velocity}};
    }
    modelGroup
    {
        type noSlip;
    }
"""
            : $$"""
    inlet
    {
        type fixedValue;
        value uniform {{velocity}};
    }
    outlet
    {
        type inletOutlet;
        inletValue uniform (0 0 0);
        value uniform {{velocity}};
    }
    modelGroup
    {
        type noSlip;
    }
    background
    {
        type noSlip;
    }
""";
        return FoamHeader("volVectorField", "0", "U") + $$"""

dimensions [0 1 -1 0 0 0 0];
internalField uniform {{velocity}};

boundaryField
{
{{boundaries}}}
""";
    }

    private static string PField(CadProjectSettings s)
    {
        var boundaries = s.AnalysisType == CadAnalysisType.ExternalFlow
            ? """
    inlet
    {
        type zeroGradient;
    }
    outlet
    {
        type fixedValue;
        value uniform 0;
    }
    farField
    {
        type freestreamPressure;
        freestreamValue uniform 0;
        value uniform 0;
    }
    modelGroup
    {
        type zeroGradient;
    }
"""
            : """
    inlet
    {
        type zeroGradient;
    }
    outlet
    {
        type fixedValue;
        value uniform 0;
    }
    modelGroup
    {
        type zeroGradient;
    }
    background
    {
        type zeroGradient;
    }
""";
        return FoamHeader("volScalarField", "0", "p") + $$"""

dimensions [0 2 -2 0 0 0 0];
internalField uniform 0;

boundaryField
{
{{boundaries}}}
""";
    }

    private static string KField(CadProjectSettings s, double k)
    {
        var scalar = F(k);
        var outerPatch = s.AnalysisType == CadAnalysisType.ExternalFlow
            ? $"    farField {{ type freestream; freestreamValue uniform {scalar}; value uniform {scalar}; }}\n"
            : $"    background {{ type kqRWallFunction; value uniform {scalar}; }}\n";
        return FoamHeader("volScalarField", "0", "k") + $$"""

dimensions [0 2 -2 0 0 0 0];
internalField uniform {{scalar}};

boundaryField
{
    inlet { type fixedValue; value uniform {{scalar}}; }
    outlet { type inletOutlet; inletValue uniform {{scalar}}; value uniform {{scalar}}; }
{{outerPatch}}
    modelGroup { type kqRWallFunction; value uniform {{scalar}}; }
}
""";
    }

    private static string OmegaField(CadProjectSettings s, double omega)
    {
        var scalar = F(omega);
        var outerPatch = s.AnalysisType == CadAnalysisType.ExternalFlow
            ? $"    farField {{ type freestream; freestreamValue uniform {scalar}; value uniform {scalar}; }}\n"
            : $"    background {{ type omegaWallFunction; value uniform {scalar}; }}\n";
        return FoamHeader("volScalarField", "0", "omega") + $$"""

dimensions [0 0 -1 0 0 0 0];
internalField uniform {{scalar}};

boundaryField
{
    inlet { type fixedValue; value uniform {{scalar}}; }
    outlet { type inletOutlet; inletValue uniform {{scalar}}; value uniform {{scalar}}; }
{{outerPatch}}
    modelGroup { type omegaWallFunction; value uniform {{scalar}}; }
}
""";
    }

    private static string NutField(CadProjectSettings s)
    {
        var outerPatch = s.AnalysisType == CadAnalysisType.ExternalFlow
            ? "    farField { type calculated; value uniform 0; }\n"
            : "    background { type nutkWallFunction; value uniform 0; }\n";
        return FoamHeader("volScalarField", "0", "nut") + $$"""

dimensions [0 2 -1 0 0 0 0];
internalField uniform 0;

boundaryField
{
    inlet { type calculated; value uniform 0; }
    outlet { type calculated; value uniform 0; }
{{outerPatch}}
    modelGroup { type nutkWallFunction; value uniform 0; }
}
""";
    }

    private static string CreatePatchDict(CadProjectSettings s, GeometryBounds g)
    {
        // The selection slab must be thinner than the locally refined cell size;
        // otherwise createPatch also sees internal faces and correctly refuses to repatch them.
        var tolerance = Math.Max(g.CharacteristicLength * 1e-4, 1e-9);
        var (minBox, maxBox) = EndBoxes(s.FlowAxis, g, tolerance);
        return FoamHeader("dictionary", "system", "createPatchDict") + $$"""

patches
{
    inlet
    {
        patchInfo { type patch; }
        constructFrom zone;
        zone
        {
            type box;
            box {{minBox.Min}}{{minBox.Max}};
        }
    }

    outlet
    {
        patchInfo { type patch; }
        constructFrom zone;
        zone
        {
            type box;
            box {{maxBox.Min}}{{maxBox.Max}};
        }
    }
}
""";
    }

    private static (GeometryBounds inlet, GeometryBounds outlet) EndBoxes(
        FlowAxis axis,
        GeometryBounds g,
        double t)
    {
        var xMin = new GeometryBounds(
            new Point3(g.Min.X - t, g.Min.Y - t, g.Min.Z - t),
            new Point3(g.Min.X + t, g.Max.Y + t, g.Max.Z + t));
        var xMax = new GeometryBounds(
            new Point3(g.Max.X - t, g.Min.Y - t, g.Min.Z - t),
            new Point3(g.Max.X + t, g.Max.Y + t, g.Max.Z + t));
        var yMin = new GeometryBounds(
            new Point3(g.Min.X - t, g.Min.Y - t, g.Min.Z - t),
            new Point3(g.Max.X + t, g.Min.Y + t, g.Max.Z + t));
        var yMax = new GeometryBounds(
            new Point3(g.Min.X - t, g.Max.Y - t, g.Min.Z - t),
            new Point3(g.Max.X + t, g.Max.Y + t, g.Max.Z + t));
        var zMin = new GeometryBounds(
            new Point3(g.Min.X - t, g.Min.Y - t, g.Min.Z - t),
            new Point3(g.Max.X + t, g.Max.Y + t, g.Min.Z + t));
        var zMax = new GeometryBounds(
            new Point3(g.Min.X - t, g.Min.Y - t, g.Max.Z - t),
            new Point3(g.Max.X + t, g.Max.Y + t, g.Max.Z + t));

        return axis switch
        {
            FlowAxis.PositiveX => (xMin, xMax),
            FlowAxis.NegativeX => (xMax, xMin),
            FlowAxis.PositiveY => (yMin, yMax),
            FlowAxis.NegativeY => (yMax, yMin),
            FlowAxis.PositiveZ => (zMin, zMax),
            _ => (zMax, zMin)
        };
    }

    private static (double k, double omega) TurbulenceValues(CadProjectSettings s)
    {
        var intensity = Math.Max(1e-6, s.TurbulenceIntensityPercent / 100);
        var k = 1.5 * Math.Pow(s.Velocity * intensity, 2);
        var omega = Math.Sqrt(k) / (Math.Pow(0.09, 0.25) * Math.Max(1e-9, s.TurbulenceLengthScale));
        return (k, omega);
    }

    private static Point3 ExternalKeepPoint(FlowAxis axis, GeometryBounds d)
    {
        var x25 = d.Min.X + d.XLength * 0.23;
        var y25 = d.Min.Y + d.YLength * 0.23;
        var z25 = d.Min.Z + d.ZLength * 0.23;
        var x77 = d.Min.X + d.XLength * 0.77;
        var y77 = d.Min.Y + d.YLength * 0.77;
        var z77 = d.Min.Z + d.ZLength * 0.77;
        return axis switch
        {
            FlowAxis.PositiveX => new Point3(x25, y25, z25),
            FlowAxis.NegativeX => new Point3(x77, y25, z25),
            FlowAxis.PositiveY => new Point3(x25, y25, z25),
            FlowAxis.NegativeY => new Point3(x25, y77, z25),
            FlowAxis.PositiveZ => new Point3(x25, y25, z25),
            _ => new Point3(x25, y25, z77)
        };
    }

    public static Point3 ParsePoint(string text)
    {
        var normalized = text.Trim().Trim('(', ')').Replace(',', ' ');
        var parts = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 ||
            !double.TryParse(parts[0], NumberStyles.Float, Inv, out var x) ||
            !double.TryParse(parts[1], NumberStyles.Float, Inv, out var y) ||
            !double.TryParse(parts[2], NumberStyles.Float, Inv, out var z))
            throw new ArgumentException("내부 유체점은 미터 단위의 `x y z` 세 값이어야 합니다.");
        return new Point3(x, y, z);
    }

    private static (string inlet, string outlet) AxisPatchNames(FlowAxis axis) => axis switch
    {
        FlowAxis.PositiveX => ("xMin", "xMax"),
        FlowAxis.NegativeX => ("xMax", "xMin"),
        FlowAxis.PositiveY => ("yMin", "yMax"),
        FlowAxis.NegativeY => ("yMax", "yMin"),
        FlowAxis.PositiveZ => ("zMin", "zMax"),
        _ => ("zMax", "zMin")
    };

    private static Point3 FlowVector(FlowAxis axis, double value) => axis switch
    {
        FlowAxis.PositiveX => new Point3(value, 0, 0),
        FlowAxis.NegativeX => new Point3(-value, 0, 0),
        FlowAxis.PositiveY => new Point3(0, value, 0),
        FlowAxis.NegativeY => new Point3(0, -value, 0),
        FlowAxis.PositiveZ => new Point3(0, 0, value),
        _ => new Point3(0, 0, -value)
    };

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

    private static string BuildProjectManifest(
        CadProjectSettings s,
        GeometryBounds g,
        GeometryBounds d,
        long baseCells) => $$"""
Foam Workbench CAD Project
Generated: {{DateTimeOffset.Now:O}}
Source CAD: {{s.CadFilePath}}
CAD unit: {{s.CadUnit}}
Analysis: {{s.AnalysisType}}
Flow axis: {{s.FlowAxis}}
Geometry bounds [m]: {{g.Min}} .. {{g.Max}}
Domain bounds [m]: {{d.Min}} .. {{d.Max}}
Estimated background cells: {{baseCells}}
Surface refinement: {{s.SurfaceRefinementMin}} .. {{s.SurfaceRefinementMax}}
Boundary layers: {{s.BoundaryLayerCount}}
Max global cells: {{s.MaxGlobalCells}}
Kinematic viscosity [m2/s]: {{F(s.KinematicViscosity)}}
Density [kg/m3]: {{F(s.Density)}}
Force patches: {{s.ForcePatches}}
Forces (pressure + viscous): {{s.CalculateForces}}
Force coefficients (Cd/Cl/Cm): {{s.CalculateForceCoefficients}}
Wall shear stress: {{s.CalculateWallShearStress}}
yPlus: {{s.CalculateYPlus}}
Q criterion: {{s.CalculateQCriterion}}
Vorticity: {{s.CalculateVorticity}}
Turbulence intensity: {{s.CalculateTurbulenceIntensity}}
Field average: {{s.CalculateFieldAverage}} ({{s.AveragedFields}})
Custom functionObjects: {{(!string.IsNullOrWhiteSpace(s.CustomFunctionObjects))}}

The STEP/IGES/BREP surface was read by Gmsh/OpenCASCADE and converted to STL.
Meshing and flow calculation are executed by the original OpenFOAM utilities.
All generated dictionaries remain editable in Foam Workbench.
""";

    private static void Write(string directory, string name, string text) =>
        File.WriteAllText(Path.Combine(directory, name), text.Replace("\r\n", "\n"), new UTF8Encoding(false));

    private static string F(double value) => value.ToString("G17", Inv);
}
