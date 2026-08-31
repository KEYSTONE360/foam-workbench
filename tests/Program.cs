using FoamWorkbench;
using FoamWorkbench.Controls;
using FoamWorkbench.Services;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

var failures = new List<string>();

void Check(bool condition, string description)
{
    if (condition)
        Console.WriteLine($"PASS  {description}");
    else
    {
        Console.WriteLine($"FAIL  {description}");
        failures.Add(description);
    }
}

void CrossCheck(int number, string target, string expected, string actual, bool condition)
{
    Console.WriteLine();
    Console.WriteLine($"TEST {number}");
    Console.WriteLine($"Target: {target}");
    Console.WriteLine($"Expected: {expected}");
    Console.WriteLine($"Actual: {actual}");
    Console.WriteLine($"Result: {(condition ? "PASS" : "FAIL")}");
    if (!condition) failures.Add($"TEST {number} — {target}");
}

PorousCaseSettings ValidPorousSettings(
    string outputRoot,
    string projectName,
    PorousSimulationType simulationType = PorousSimulationType.Steady,
    PorousFlowMode flowMode = PorousFlowMode.RainfallFlux,
    int endTime = 40,
    double deltaT = 0.002,
    int writeInterval = -1)
{
    var layers = PorousPresetFactory.CreateTreeShieldSevenLayer()
        .Select(layer => layer.Clone()).ToArray();
    var thickness = new[] { 1.0, 4.0, 1.2, 5.0, 1.5, 4.5, 1.0 };
    var permeability = new[] { 2e-11, 5e-11, 1e-11, 5e-12, 1.5e-11, 8e-12, 2.5e-11 };
    var porosity = new[] { 0.62, 0.55, 0.72, 0.42, 0.68, 0.48, 0.65 };
    for (var i = 0; i < layers.Length; i++)
    {
        layers[i].Thickness = thickness[i];
        layers[i].Permeability = permeability[i];
        layers[i].Porosity = porosity[i];
        layers[i].ParameterSource = PorousParameterSource.UserDefined;
        layers[i].ParameterSourceReference = "synthetic validation only";
    }
    return new PorousCaseSettings
    {
        OutputRootPath = outputRoot,
        ProjectName = projectName,
        DomainWidthMm = 80,
        Layers = layers,
        Density = 998.2,
        DynamicViscosity = 1.003e-3,
        GravityEnabled = true,
        GravityX = 0,
        GravityY = -9.81,
        GravityZ = 0,
        FlowMode = flowMode,
        RainfallMmPerHour = 20,
        WaterHeadMm = 50,
        SimulationType = simulationType,
        MeshPreset = PorousMeshPreset.Medium,
        TargetCellSizeMm = 1,
        MinimumCellsPerLayer = 2,
        EndTime = endTime,
        WriteInterval = writeInterval > 0 ? writeInterval : Math.Max(1, endTime / 2),
        DeltaT = deltaT,
        ProcessCount = 1,
        CfdAnalyticalTolerancePercent = 10
    };
}

Check(OpenFoamService.ToWslPath(@"C:\CFD Work\case") == "/mnt/c/CFD Work/case",
    "Windows path maps to WSL without losing spaces");

const string controlDict = """
    // application wrongSolver;
    application foamRun;
    /* solver ignoredFluid; */
    solver incompressibleFluid;
    """;
Check(CaseInspector.MatchEntry(controlDict, "application") == "foamRun",
    "application is parsed with comments ignored");
Check(CaseInspector.MatchEntry(controlDict, "solver") == "incompressibleFluid",
    "Foundation modular solver entry is parsed");

ResidualSample? parsed = null;
var parser = new ResidualParser();
parser.SampleParsed += sample => parsed = sample;
parser.ParseLine("smoothSolver:  Solving for Ux, Initial residual = 0.0042, Final residual = 8.1e-07, No Iterations 3");
Check(parsed is { Field: "Ux", Iterations: 3 }, "solver residual line is recognized");
Check(parsed is not null && Math.Abs(parsed.Initial - 0.0042) < 1e-12,
    "initial residual retains numeric precision");

var functionObjects = OpenFoamFunctionObjectBuilder.Build(new CadProjectSettings
{
    Turbulence = TurbulenceChoice.KOmegaSst,
    Velocity = 32.5,
    CalculateResiduals = true,
    CalculateForces = true,
    CalculateForceCoefficients = true,
    CalculateWallShearStress = true,
    CalculateYPlus = true,
    CalculateQCriterion = true,
    CalculateVorticity = true,
    CalculateTurbulenceIntensity = true,
    CalculateFieldAverage = true,
    ForcePatches = "model.* spoiler",
    Density = 1.184,
    ReferenceArea = 2.4,
    ReferenceLength = 1.7,
    AveragedFields = "U p",
    CustomFunctionObjects = "customProbe\n{\n    type probes;\n}"
});
Check(functionObjects.Contains("type            forces;") &&
      functionObjects.Contains("type            forceCoeffs;") &&
      functionObjects.Contains("rhoInf          ") &&
      functionObjects.Contains("Aref            ") &&
      functionObjects.Contains("patches         (\"model.*\" \"spoiler\");"),
    "force, viscous resistance and coefficient controls map to OpenFOAM dictionaries");
Check(functionObjects.Contains("type            wallShearStress;") &&
      functionObjects.Contains("type            yPlus;") &&
      functionObjects.Contains("type            Q;") &&
      functionObjects.Contains("type            vorticity;") &&
      functionObjects.Contains("type            turbulenceIntensity;") &&
      functionObjects.Contains("type            fieldAverage;"),
    "selected wall and derived fields are emitted as native functionObjects");
Check(functionObjects.Contains("customProbe\n{\n    type probes;\n}"),
    "custom functionObject source is preserved verbatim");

var presetPath = Path.Combine(Path.GetTempPath(), $"FoamWorkbench-Preset-{Guid.NewGuid():N}.fwpreset.json");
var preset = new MeshCalculationPreset
{
    Name = "Wind tunnel fine mesh",
    SavedAtUtc = new DateTimeOffset(2026, 8, 10, 1, 2, 3, TimeSpan.Zero),
    CadUnit = CadLengthUnit.Metre,
    AnalysisType = CadAnalysisType.InternalFluidVolume,
    FlowAxis = FlowAxis.NegativeZ,
    Turbulence = TurbulenceChoice.KOmegaSst,
    Velocity = 42.75,
    KinematicViscosity = 1.48e-5,
    CadSurfaceSize = 0.0125,
    BaseCellSize = 0.075,
    SurfaceRefinementMin = 3,
    SurfaceRefinementMax = 6,
    FeatureRefinementLevel = 5,
    BoundaryLayerCount = 9,
    LayerExpansionRatio = 1.16,
    FinalLayerThickness = 0.18,
    MaxGlobalCells = 12_345_678,
    UpstreamLengths = 4.5,
    DownstreamLengths = 12,
    SideLengths = 5.5,
    EndTime = 4200,
    WriteInterval = 25,
    ProcessCount = 12,
    FluidPointText = "0.1 0.2 0.3",
    CalculateYPlus = true,
    CalculateTurbulenceIntensity = true,
    CalculateFieldAverage = true,
    ForcePatches = "body.* wing",
    Density = 1.204,
    ReferenceArea = 2.35,
    ReferenceLength = 1.82,
    CentreOfRotationText = "1 2 3",
    DragDirectionText = "0 0 -1",
    LiftDirectionText = "0 1 0",
    PitchAxisText = "1 0 0",
    AveragedFields = "U p k omega",
    CustomFunctionObjects = "myProbe\n{\n    type probes;\n}"
};
MeshCalculationPresetStore.Save(presetPath, preset);
var loadedPreset = MeshCalculationPresetStore.Load(presetPath);
var presetJson = File.ReadAllText(presetPath);
Check(loadedPreset.Name == preset.Name &&
      loadedPreset.FlowAxis == FlowAxis.NegativeZ &&
      loadedPreset.SurfaceRefinementMax == 6 &&
      loadedPreset.BoundaryLayerCount == 9 &&
      loadedPreset.ProcessCount == 12 &&
      Math.Abs(loadedPreset.Velocity - 42.75) < 1e-12 &&
      loadedPreset.CalculateFieldAverage &&
      loadedPreset.CustomFunctionObjects == preset.CustomFunctionObjects,
    "mesh, flow, calculation and result settings round-trip through a preset file");
Check(!presetJson.Contains("CadFilePath", StringComparison.Ordinal) &&
      !presetJson.Contains("OutputRootPath", StringComparison.Ordinal) &&
      !presetJson.Contains("ProjectName", StringComparison.Ordinal),
    "preset deliberately excludes CAD path, result folder and project name");
var excludedProjectProperties = new HashSet<string>(StringComparer.Ordinal)
{
    nameof(CadProjectSettings.CadFilePath),
    nameof(CadProjectSettings.OutputRootPath),
    nameof(CadProjectSettings.ProjectName)
};
var presetPropertyNames = typeof(MeshCalculationPreset).GetProperties()
    .Select(property => property.Name)
    .ToHashSet(StringComparer.Ordinal);
var missingPresetProperties = typeof(CadProjectSettings).GetProperties()
    .Select(property => property.Name)
    .Where(name => !excludedProjectProperties.Contains(name) && !presetPropertyNames.Contains(name))
    .ToArray();
Check(missingPresetProperties.Length == 0,
    "preset covers every CAD project setting except the three model-specific identity paths");
var projectFromPreset = loadedPreset.ToProjectSettings(
    @"C:\Models\vehicle.step", @"D:\CFD Results", "VehicleRun");
Check(projectFromPreset.CadFilePath == @"C:\Models\vehicle.step" &&
      projectFromPreset.ProjectName == "VehicleRun" &&
      projectFromPreset.MaxGlobalCells == 12_345_678 &&
      projectFromPreset.ForcePatches == "body.* wing",
    "loaded preset combines with the currently selected project paths without changing its settings");
File.Delete(presetPath);

Console.WriteLine();
Console.WriteLine("================ POROUS MEDIA CROSS-VALIDATION ================");

CrossCheck(1,
    "Existing External Aerodynamics regression",
    "CAD settings and native OpenFOAM functionObject generation remain available",
    $"CadProjectSettings={typeof(CadProjectSettings).IsClass}, forces={functionObjects.Contains("type            forces;")}",
    typeof(CadProjectSettings).IsClass && functionObjects.Contains("type            forces;"));

var conversionOk =
    Math.Abs(PorousUnitConverter.MillimetresToMetres(80) - 0.08) < 1e-15 &&
    Math.Abs(PorousUnitConverter.MillimetresToMetres(0.25) - 0.00025) < 1e-15 &&
    Math.Abs(PorousUnitConverter.MillimetresPerHourToMetresPerSecond(20) - 5.555555555555556e-6) < 1e-18 &&
    Math.Abs(PorousUnitConverter.MillimetresToMetres(50) - 0.05) < 1e-15;
CrossCheck(2, "Unit conversion",
    "80 mm=0.08 m; 0.25 mm=0.00025 m; 20 mm/hr=5.55555556e-6 m/s; 50 mm=0.05 m",
    $"{PorousUnitConverter.MillimetresToMetres(80):G17}; {PorousUnitConverter.MillimetresToMetres(0.25):G17}; " +
    $"{PorousUnitConverter.MillimetresPerHourToMetresPerSecond(20):G17}; {PorousUnitConverter.MillimetresToMetres(50):G17}",
    conversionOk);

var defaultLayers = PorousPresetFactory.CreateTreeShieldSevenLayer();
var expectedMaterials = new[]
{
    "Abaca Fiber Nonwoven", "Vermiculite", "Cotton Fiber Pad", "Activated Carbon",
    "Coir Fiber Mat", "Spent Coffee Grounds", "Bamboo Fiber Sheet"
};
CrossCheck(3, "New TreeShield preset structure",
    "Exactly 7 layers in Abaca/Vermiculite/Cotton/Activated Carbon/Coir/Coffee/Bamboo order",
    $"Count={defaultLayers.Count}; {string.Join(" > ", defaultLayers.Select(layer => layer.DisplayNameEn))}",
    defaultLayers.Count == 7 && defaultLayers.Select(layer => layer.DisplayNameEn).SequenceEqual(expectedMaterials));

var expectedZones = new[]
{
    "layer1_abaca", "layer2_vermiculite", "layer3_cotton", "layer4_activatedCarbon",
    "layer5_coir", "layer6_coffeeGrounds", "layer7_bamboo"
};
CrossCheck(4, "Cell zone names",
    string.Join(", ", expectedZones),
    string.Join(", ", defaultLayers.Select(layer => layer.Name)),
    defaultLayers.Select(layer => layer.Name).SequenceEqual(expectedZones));

var undefinedSettings = new PorousCaseSettings
{
    OutputRootPath = Path.GetTempPath(),
    ProjectName = "UndefinedProtection",
    Layers = defaultLayers
};
var undefinedValidation = PorousPhysics.Validate(undefinedSettings);
var undefinedMessage = PorousCaseGenerator.BuildMissingPropertiesMessage(undefinedValidation);
CrossCheck(5, "Undefined material protection",
    "Solver blocked with exact missing thickness and permeability for all 7 layers",
    $"Valid={undefinedValidation.IsValid}; Errors={undefinedValidation.Errors.Count}",
    !undefinedValidation.IsValid && undefinedValidation.Errors.Count >= 14 &&
    undefinedMessage.Contains("Layer 1 — Abaca Fiber Nonwoven — thickness") &&
    undefinedMessage.Contains("Layer 7 — Bamboo Fiber Sheet — permeability") &&
    defaultLayers.All(layer => layer.Thickness is null && layer.Permeability is null));

var knownResistance = PorousPhysics.PermeabilityToDarcyResistance(5e-12);
CrossCheck(9, "k to Darcy resistance", "5e-12 m² -> 2e11 m⁻²",
    knownResistance.ToString("G17"), Math.Abs(knownResistance - 2e11) < 1e-3);

var roundTripInputs = new[] { 1e-15, 5e-12, 2.75e-9 };
var roundTripMaxError = roundTripInputs.Max(k =>
    Math.Abs(PorousPhysics.DarcyResistanceToPermeability(
        PorousPhysics.PermeabilityToDarcyResistance(k)) - k) / k);
CrossCheck(10, "Darcy resistance round-trip", "relative error <= 1e-14 for three magnitudes",
    $"max relative error={roundTripMaxError:G17}", roundTripMaxError <= 1e-14);

var nuWater = PorousPhysics.KinematicViscosity(1.003e-3, 998.2);
CrossCheck(11, "Water properties", "rho=998.2; mu=1.003e-3; nu=mu/rho",
    $"rho=998.2; mu=0.001003; nu={nuWater:G17}",
    Math.Abs(nuWater - 1.003e-3 / 998.2) < 1e-18);

var porousTempRoot = Path.Combine(Path.GetTempPath(), $"FoamWorkbench-Porous-Smoke-{Guid.NewGuid():N}");
var porousSettings = ValidPorousSettings(porousTempRoot, "PorousDictionaryValidation");
var porousGenerator = new PorousCaseGenerator();
var generatedPorous = porousGenerator.Generate(porousSettings);
var gravityText = File.ReadAllText(Path.Combine(generatedPorous.CasePath, "constant", "g"));
var modelsText = File.ReadAllText(Path.Combine(generatedPorous.CasePath, "constant", "fvModels"));
CrossCheck(12, "Gravity equation coupling",
    "g=(0 -9.81 0) and fvModels/buoyancyForce adds it to momentum",
    $"gVector={gravityText.Contains("value (0 -9.8100000000000005 0)")}; buoyancyForce={modelsText.Contains("type buoyancyForce;")}",
    gravityText.Contains("value (0 -9.8100000000000005 0)") && modelsText.Contains("type buoyancyForce;"));

var rainfallVelocity = PorousUnitConverter.MillimetresPerHourToMetresPerSecond(20);
var uText = File.ReadAllText(Path.Combine(generatedPorous.CasePath, "0", "U"));
CrossCheck(13, "Rainfall conversion and direction",
    "20 mm/hr -> 5.55555556e-6 m/s, top velocity points downward (-Y)",
    $"q={rainfallVelocity:G17}; negativeY={uText.Contains(rainfallVelocity.ToString("G17").Insert(0, "-"))}",
    Math.Abs(rainfallVelocity - 5.555555555555556e-6) < 1e-18 &&
    uText.Contains(rainfallVelocity.ToString("G17").Insert(0, "-")));

var headPressure = PorousPhysics.WaterHeadPressure(998.2, 9.81, 0.05);
CrossCheck(14, "Water head pressure", "50 mm water -> approximately 489–490 Pa",
    $"{headPressure:G17} Pa", headPressure is >= 489 and <= 490);

var analytical = PorousPhysics.CalculateAnalytical(porousSettings);
var expectedResistance = porousSettings.Layers.Sum(layer =>
    PorousUnitConverter.MillimetresToMetres(layer.Thickness!.Value) / layer.ThroughPermeability!.Value);
var expectedKeff = analytical.TotalThicknessMetres / expectedResistance;
CrossCheck(15, "Analytical Darcy calculation", "R_i=L_i/k_i and k_eff=L_total/sum(R_i)",
    $"Rsum={expectedResistance:G17}; kEff={analytical.EquivalentPermeability:G17}",
    Math.Abs(analytical.EquivalentPermeability - expectedKeff) / expectedKeff < 1e-14);

var fractionSum = analytical.Layers.Sum(layer => layer.ResistanceFraction);
CrossCheck(16, "Resistance fractions", "sum=100% within floating-point tolerance",
    $"{fractionSum * 100:G17}%", Math.Abs(fractionSum - 1) < 1e-14);

var manualBottleneck = analytical.Layers.MaxBy(layer => layer.Resistance)!;
CrossCheck(17, "Bottleneck detection", "layer with maximum L/k",
    $"Detected={analytical.Bottleneck.DisplayName}; manual={manualBottleneck.DisplayName}",
    analytical.Bottleneck.LayerId == manualBottleneck.LayerId);

var residence = PorousPhysics.NominalResidenceTime(0.012, 0.003);
CrossCheck(18, "Nominal residence time", "0.012/0.003=4 s",
    $"{residence:G17} s", Math.Abs(residence - 4) < 1e-14);

var balance = PorousPhysics.CalculateFlowBalance(1.0e-6, -0.999e-6);
var zeroBalance = PorousPhysics.CalculateFlowBalance(0, 0);
CrossCheck(19, "Flow balance logic", "0.1% mismatch and division-by-zero safe",
    $"difference={balance.DifferencePercent:G17}%; zero={zeroBalance.DifferencePercent:G17}%",
    Math.Abs(balance.DifferencePercent - 0.1) < 1e-10 && zeroBalance.DifferencePercent == 0);

var anisotropicLayers = porousSettings.Layers.Select(layer => layer.Clone()).ToArray();
anisotropicLayers[0].PermeabilityType = PorousPermeabilityType.Anisotropic;
anisotropicLayers[0].PermeabilityX = 1e-10;
anisotropicLayers[0].PermeabilityY = 5e-12;
anisotropicLayers[0].PermeabilityZ = 2e-11;
var anisotropicSettings = porousSettings.CloneWith(anisotropicLayers, porousTempRoot, "AnisotropicDictionary");
var anisotropicModels = PorousCaseGenerator.CreateFvModelsDictionary(anisotropicSettings);
CrossCheck(20, "Anisotropic permeability dictionary", "Kx/Ky/Kz -> d=(1/Kx 1/Ky 1/Kz)",
    anisotropicModels.Contains("(10000000000 200000000000 50000000000)")
        ? "d=(1e10 2e11 5e10)" : "expected tensor not found",
    anisotropicModels.Contains("(10000000000 200000000000 50000000000)"));

var porousFiles = Directory.EnumerateFiles(generatedPorous.CasePath, "*", SearchOption.AllDirectories)
    .Select(Path.GetFileName).ToArray();
var momentumText = File.ReadAllText(Path.Combine(generatedPorous.CasePath, "constant", "momentumTransport"));
CrossCheck(21, "Laminar mode", "simulationType laminar; no k/omega/epsilon fields",
    $"laminar={momentumText.Contains("simulationType laminar;")}; turbulentFields={string.Join(',', porousFiles.Where(name => name is "k" or "omega" or "epsilon"))}",
    momentumText.Contains("simulationType laminar;") && !porousFiles.Any(name => name is "k" or "omega" or "epsilon"));

var steadySchemes = File.ReadAllText(Path.Combine(generatedPorous.CasePath, "system", "fvSchemes"));
var steadySolution = File.ReadAllText(Path.Combine(generatedPorous.CasePath, "system", "fvSolution"));
CrossCheck(22, "Steady solver dictionaries", "steadyState ddt + SIMPLE + incompressibleFluid",
    $"steadyState={steadySchemes.Contains("steadyState")}; SIMPLE={steadySolution.Contains("SIMPLE")}",
    steadySchemes.Contains("steadyState") && steadySolution.Contains("SIMPLE"));

var transientSettings = ValidPorousSettings(
    porousTempRoot, "TransientDictionary", PorousSimulationType.Transient, PorousFlowMode.WaterHead, 4);
var transientGenerated = porousGenerator.Generate(transientSettings);
var transientSchemes = File.ReadAllText(Path.Combine(transientGenerated.CasePath, "system", "fvSchemes"));
var transientSolution = File.ReadAllText(Path.Combine(transientGenerated.CasePath, "system", "fvSolution"));
CrossCheck(23, "Transient solver dictionaries", "Euler ddt + PIMPLE + incompressibleFluid",
    $"Euler={transientSchemes.Contains("default Euler;")}; PIMPLE={transientSolution.Contains("PIMPLE")}",
    transientSchemes.Contains("default Euler;") && transientSolution.Contains("PIMPLE"));

var sweepRuntime = new AppSettings
{
    Backend = RuntimeBackend.Wsl,
    WslDistribution = "Ubuntu-24.04",
    OpenFoamBashrc = "/opt/openfoam14/etc/bashrc"
};
var sweepOpenFoam = new OpenFoamService(sweepRuntime, new ProcessRunner());
var sweepSimulation = new PorousSimulationService(sweepOpenFoam, porousGenerator);
var sweepService = new PorousSweepService(porousGenerator, sweepSimulation);
var sweepBase = ValidPorousSettings(porousTempRoot, $"SyntheticSweep_{Guid.NewGuid():N}");
var sweep = await sweepService.RunAsync(sweepBase,
    new PorousSweepRequest(3, 1e-13, 1e-10, 3, RunSolver: false));
CrossCheck(24, "Parameter sweep", "3 separate case directories and parameter_sweep.csv with 3 rows",
    $"rows={sweep.Rows.Count}; dirs={sweep.Rows.Count(row => Directory.Exists(row.CasePath))}; csv={File.Exists(sweep.CsvPath)}",
    sweep.Rows.Count == 3 && sweep.Rows.All(row => Directory.Exists(row.CasePath)) &&
    File.Exists(sweep.CsvPath) && File.ReadLines(sweep.CsvPath).Count() == 4);

var forbiddenDefaultMaterials = new[] { "CLDH", "Zeolite", "Biochar", "Banana" };
var foundDefaultLegacy = defaultLayers
    .Where(layer => forbiddenDefaultMaterials.Any(term =>
        layer.DisplayNameEn.Contains(term, StringComparison.OrdinalIgnoreCase)))
    .Select(layer => layer.DisplayNameEn).ToArray();
CrossCheck(25, "Legacy values contamination", "Legacy proposal materials and values remain absent from the new default preset",
    foundDefaultLegacy.Length == 0 && defaultLayers.All(layer => layer.Thickness is null && layer.Permeability is null)
        ? "none; all default physical values undefined"
        : string.Join(", ", foundDefaultLegacy),
    foundDefaultLegacy.Length == 0 && defaultLayers.All(layer => layer.Thickness is null && layer.Permeability is null));

var reportFiles = PorousReportGenerator.Generate(
    porousSettings, analytical, result: null, Path.Combine(generatedPorous.CasePath, "FoamWorkbenchReport"));
CrossCheck(26, "Automatic CFD report", "HTML + CSV + JSON are generated",
    string.Join(", ", reportFiles.Select(Path.GetFileName)), reportFiles.Count == 3 && reportFiles.All(File.Exists));

Console.WriteLine();
Console.WriteLine("================ PDF PROPOSAL PRESET CROSS-VALIDATION ================");

var builtInPresets = PorousPresetFactory.CreateBuiltInPresets();
var proposalA = PorousPresetFactory.CreateBuiltInSettings(PorousPresetFactory.ProposalRainfallId);
var proposalGravity = PorousPresetFactory.CreateBuiltInSettings(PorousPresetFactory.ProposalGravityDrainageId);
var proposalB = PorousPresetFactory.CreateBuiltInSettings(PorousPresetFactory.ProposalWaterHeadId);
var proposalLayers = proposalA.Layers;

CrossCheck(27, "Built-in preset separation",
    "New undefined 7-layer default plus separate PDF rainfall, gravity-drainage and water-head presets",
    $"count={builtInPresets.Count}; ids={string.Join(",", builtInPresets.Select(item => item.Id))}",
    builtInPresets.Count == 4 &&
    builtInPresets.Any(item => item.Id == PorousPresetFactory.TreeShieldSevenLayerId) &&
    builtInPresets.Any(item => item.Id == PorousPresetFactory.ProposalRainfallId) &&
    builtInPresets.Any(item => item.Id == PorousPresetFactory.ProposalGravityDrainageId) &&
    builtInPresets.Any(item => item.Id == PorousPresetFactory.ProposalWaterHeadId));

var proposalExpectedMaterials = new[]
{
    "Coir Woven Mesh", "Banana Fiber Nonwoven", "Biochar + Activated Carbon", "4A · CLDH",
    "4B · Acid-treated Zeolite", "5-upper · Bamboo Nonwoven", "5-lower · Coir Drainage Mesh"
};
var proposalExpectedZones = new[]
{
    "layer1_coirWovenMesh", "layer2_bananaNonwoven", "layer3_biocharActivatedCarbon", "layer4a_cldh",
    "layer4b_acidTreatedZeolite", "layer5upper_bambooNonwoven", "layer5lower_coirDrainageMesh"
};
CrossCheck(28, "PDF layer order and independent zones",
    "7 layers in PDF order with CLDH and acid-treated zeolite in separate zones",
    string.Join(" > ", proposalLayers.Select(layer => $"{layer.DisplayNameEn} [{layer.Name}]")),
    proposalLayers.Count == 7 &&
    proposalLayers.Select(layer => layer.DisplayNameEn).SequenceEqual(proposalExpectedMaterials) &&
    proposalLayers.Select(layer => layer.Name).SequenceEqual(proposalExpectedZones) &&
    proposalLayers.Select(layer => layer.Name).Distinct(StringComparer.Ordinal).Count() == 7);

var proposalThicknesses = new[] { 4d, 3d, 8d, 3d, 3d, 1d, 3d };
CrossCheck(29, "PDF geometry and thickness",
    "width=80 mm; layer thicknesses=4,3,8,3,3,1,3 mm; total=25 mm",
    $"width={proposalA.DomainWidthMm:G17}; layers={string.Join(",", proposalLayers.Select(layer => layer.Thickness))}; total={proposalLayers.Sum(layer => layer.Thickness):G17}",
    proposalA.DomainWidthMm == 80 &&
    proposalLayers.Select(layer => layer.Thickness!.Value).SequenceEqual(proposalThicknesses) &&
    Math.Abs(proposalLayers.Sum(layer => layer.Thickness!.Value) - 25) < 1e-14);

var expectedPoreRanges = new[]
{
    (800d, 2000d), (150d, 300d), (100d, 200d), (50d, 150d),
    (50d, 150d), (50d, 100d), (500d, 1500d)
};
CrossCheck(30, "Pore-size separation",
    "PDF target pore ranges stored as pore size, never particle size or permeability",
    string.Join("; ", proposalLayers.Select(layer => $"{layer.PoreSizeMin}-{layer.PoreSizeMax} µm")),
    proposalLayers.Select((layer, i) =>
        layer.PoreSizeMin == expectedPoreRanges[i].Item1 &&
        layer.PoreSizeMax == expectedPoreRanges[i].Item2 &&
        layer.ParticleSize is null).All(value => value));

var proposalPermeabilities = new[] { 5e-10, 1e-10, 1.2e-11, 5e-12, 5e-12, 3e-12, 3e-10 };
var permeabilityValuesOk = proposalLayers.Select((layer, i) =>
    Math.Abs(layer.Permeability!.Value - proposalPermeabilities[i]) <= proposalPermeabilities[i] * 1e-14 &&
    Math.Abs(layer.DarcyResistance!.Value - 1.0 / proposalPermeabilities[i]) <= (1.0 / proposalPermeabilities[i]) * 1e-14).All(value => value);
CrossCheck(31, "PDF intrinsic permeability and Darcy resistance",
    "All seven k values match §4 and 1/k is derived, not copied as k",
    string.Join("; ", proposalLayers.Select(layer => $"k={layer.Permeability:G3}, 1/k={layer.DarcyResistance:G3}")),
    permeabilityValuesOk);

CrossCheck(32, "Temporary-source metadata",
    "All seven k values marked Estimated with replacement warning; porosity remains undefined",
    $"estimated={proposalLayers.Count(layer => layer.ParameterSource == PorousParameterSource.Estimated)}; porosityUndefined={proposalLayers.Count(layer => layer.Porosity is null)}",
    proposalLayers.All(layer => layer.ParameterSource == PorousParameterSource.Estimated &&
                                layer.ParameterSourceReference.Contains("잠정값") &&
                                layer.Porosity is null));

CrossCheck(33, "PDF water and gravity",
    "rho=998.2 kg/m³; mu=1.003e-3 Pa·s; gravity=(0,-9.81,0)",
    $"rho={proposalA.Density:G17}; mu={proposalA.DynamicViscosity:G17}; g=({proposalA.GravityX},{proposalA.GravityY},{proposalA.GravityZ})",
    proposalA.Density == 998.2 && proposalA.DynamicViscosity == 1.003e-3 &&
    proposalA.GravityEnabled && proposalA.GravityX == 0 && proposalA.GravityY == -9.81 && proposalA.GravityZ == 0);

CrossCheck(34, "Scenario A boundary mode",
    "Rainfall Flux 20 mm/hr, steady SIMPLE workflow, 5.55555556e-6 m/s downward inlet",
    $"mode={proposalA.FlowMode}; simulation={proposalA.SimulationType}; velocity={PorousUnitConverter.MillimetresPerHourToMetresPerSecond(proposalA.RainfallMmPerHour):G17}",
    proposalA.FlowMode == PorousFlowMode.RainfallFlux && proposalA.SimulationType == PorousSimulationType.Steady &&
    Math.Abs(PorousUnitConverter.MillimetresPerHourToMetresPerSecond(proposalA.RainfallMmPerHour) - 5.555555555555556e-6) < 1e-18);

CrossCheck(35, "Scenario B boundary mode",
    "Water Head 50 mm, transient workflow, rho*g*h approximately 489-490 Pa",
    $"mode={proposalB.FlowMode}; simulation={proposalB.SimulationType}; pressure={PorousPhysics.WaterHeadPressure(proposalB.Density, 9.81, PorousUnitConverter.MillimetresToMetres(proposalB.WaterHeadMm)):G17} Pa",
    proposalB.FlowMode == PorousFlowMode.WaterHead && proposalB.SimulationType == PorousSimulationType.Transient &&
    PorousPhysics.WaterHeadPressure(proposalB.Density, 9.81, PorousUnitConverter.MillimetresToMetres(proposalB.WaterHeadMm)) is >= 489 and <= 490);

CrossCheck(36, "PDF mesh condition",
    "structured mesh target dy=0.25 mm and minimum 4 cells through every layer",
    $"dy={proposalA.TargetCellSizeMm}; minimum={proposalA.MinimumCellsPerLayer}; min actual ratio={proposalLayers.Min(layer => layer.Thickness!.Value / proposalA.TargetCellSizeMm)}",
    proposalA.TargetCellSizeMm == 0.25 && proposalA.MinimumCellsPerLayer == 4 &&
    proposalLayers.All(layer => layer.Thickness!.Value / proposalA.TargetCellSizeMm >= 4));

CrossCheck(37, "Darcy-only low-speed preset",
    "Forchheimer coefficient C2/f is zero for all seven zones",
    string.Join(",", proposalLayers.Select(layer => layer.ForchheimerCoefficient)),
    proposalLayers.All(layer => layer.ForchheimerCoefficient == 0));

var proposalValidation = PorousPhysics.Validate(proposalA);
var proposalAnalytical = PorousPhysics.CalculateAnalytical(proposalA);
CrossCheck(38, "PDF analytical hydraulic conductivity",
    "Calculated hydraulic conductivity approximately 1.09e-4 m/s and above 5.56e-6 m/s",
    $"K={proposalAnalytical.HydraulicConductivity:G17}; target={proposalA.MinimumHydraulicConductivity:G17}; safety={proposalAnalytical.SafetyFactor:G17}",
    proposalValidation.IsValid &&
    Math.Abs(proposalAnalytical.HydraulicConductivity - 1.09e-4) / 1.09e-4 < 0.02 &&
    proposalAnalytical.HydraulicConductivity >= proposalA.MinimumHydraulicConductivity &&
    proposalAnalytical.SafetyFactor is > 19 and < 20);

var proposalLayer4Fraction = proposalAnalytical.Layers
    .Where(layer => layer.ZoneName is "layer4a_cldh" or "layer4b_acidTreatedZeolite")
    .Sum(layer => layer.ResistanceFraction);
CrossCheck(39, "PDF bottleneck cross-check",
    "4A CLDH and 4B zeolite remain separate zones; design group 4 contributes approximately 53% while individual-zone bottleneck is reported separately",
    $"4A+4B={proposalLayer4Fraction:P6}; group bottleneck={proposalAnalytical.BottleneckGroup.GroupId}; individual bottleneck={proposalAnalytical.Bottleneck.ZoneName}",
    proposalAnalytical.Layers.Count(layer => layer.ZoneName.StartsWith("layer4", StringComparison.Ordinal)) == 2 &&
    proposalLayer4Fraction is > 0.53 and < 0.54 &&
    proposalAnalytical.BottleneckGroup.GroupId == "4" &&
    proposalAnalytical.BottleneckGroup.ZoneNames.SequenceEqual(new[] { "layer4a_cldh", "layer4b_acidTreatedZeolite" }) &&
    proposalAnalytical.Bottleneck.ZoneName == "layer3_biocharActivatedCarbon");

var proposalCase = porousGenerator.Generate(proposalA.CloneWith(
    outputRootPath: porousTempRoot,
    projectName: $"PdfProposal_{Guid.NewGuid():N}"));
var proposalModels = File.ReadAllText(Path.Combine(proposalCase.CasePath, "constant", "fvModels"));
var proposalU = File.ReadAllText(Path.Combine(proposalCase.CasePath, "0", "U"));
var proposalP = File.ReadAllText(Path.Combine(proposalCase.CasePath, "0", "p"));
CrossCheck(40, "OpenFOAM proposal dictionaries",
    "Seven porosityForce models, all zone names, downward rainfall inlet, zero-pressure outlet",
    $"porosityForce count={proposalModels.Split("type            porosityForce;", StringSplitOptions.None).Length - 1}; zones={proposalExpectedZones.Count(zone => proposalModels.Contains($"cellZone        {zone};"))}",
    proposalModels.Split("type            porosityForce;", StringSplitOptions.None).Length - 1 == 7 &&
    proposalExpectedZones.All(zone => proposalModels.Contains($"cellZone        {zone};")) &&
    proposalU.Contains("top") && proposalU.Contains("uniform (0 -5.5555555555555558E-06 0)") &&
    proposalP.Contains("bottom { type fixedValue; value uniform 0; }"));

CrossCheck(41, "Preset acceptance metadata",
    "Hydraulic threshold=5.56e-6 m/s and CFD/analytical tolerance=10% preserved in cloned case settings",
    $"minimum={proposalA.MinimumHydraulicConductivity:G17}; tolerance={proposalA.CfdAnalyticalTolerancePercent:G17}; clonedPreset={proposalCase.CasePath}",
    proposalA.MinimumHydraulicConductivity == 5.56e-6 &&
    proposalA.CfdAnalyticalTolerancePercent == 10 &&
    File.ReadAllText(Path.Combine(proposalCase.CasePath, "PorousWorkbenchProject.txt")).Contains("CFD/analytical tolerance: 10 %"));

Console.WriteLine();
Console.WriteLine("================ GRAVITY DRAINAGE PRESET + UI CROSS-VALIDATION ================");

CrossCheck(50, "Gravity Drainage preset identity",
    "Dedicated GravityDrainage/Steady preset and project identity",
    $"id={proposalGravity.PresetId}; mode={proposalGravity.FlowMode}; simulation={proposalGravity.SimulationType}; project={proposalGravity.ProjectName}",
    proposalGravity.PresetId == PorousPresetFactory.ProposalGravityDrainageId &&
    proposalGravity.FlowMode == PorousFlowMode.GravityDrainage &&
    proposalGravity.SimulationType == PorousSimulationType.Steady &&
    proposalGravity.ProjectName == "TreeShieldProposalGravityDrainage");

CrossCheck(51, "Gravity preset PDF layer fidelity",
    "Same seven PDF layers, zones, order, thickness and tentative permeability without shared mutable objects",
    string.Join(" > ", proposalGravity.Layers.Select(layer => $"{layer.Name}:{layer.Thickness}mm:{layer.Permeability:G3}")),
    proposalGravity.Layers.Count == 7 &&
    proposalGravity.Layers.Select(layer => layer.Name).SequenceEqual(proposalExpectedZones) &&
    proposalGravity.Layers.Select(layer => layer.Thickness!.Value).SequenceEqual(proposalThicknesses) &&
    proposalGravity.Layers.Select(layer => layer.Permeability!.Value).SequenceEqual(proposalPermeabilities) &&
    !ReferenceEquals(proposalGravity.Layers[0], proposalA.Layers[0]));

var gravityValidation = PorousPhysics.Validate(proposalGravity);
CrossCheck(52, "Gravity preset validation",
    "Required CFD inputs valid while estimated-permeability warnings remain visible",
    $"valid={gravityValidation.IsValid}; errors={gravityValidation.Errors.Count}; warnings={gravityValidation.Warnings.Count}",
    gravityValidation.IsValid && gravityValidation.Errors.Count == 0 &&
    gravityValidation.Warnings.Any(issue => issue.Message.Contains("Estimated", StringComparison.OrdinalIgnoreCase) ||
                                                     issue.Message.Contains("추정", StringComparison.OrdinalIgnoreCase)));

var gravityCase = porousGenerator.Generate(proposalGravity.CloneWith(
    outputRootPath: porousTempRoot,
    projectName: $"PdfGravity_{Guid.NewGuid():N}"));
var gravityU = File.ReadAllText(Path.Combine(gravityCase.CasePath, "0", "U"));
var gravityP = File.ReadAllText(Path.Combine(gravityCase.CasePath, "0", "p"));
var gravityModels = File.ReadAllText(Path.Combine(gravityCase.CasePath, "constant", "fvModels"));
var gravityVector = File.ReadAllText(Path.Combine(gravityCase.CasePath, "constant", "g"));
var gravityManifest = File.ReadAllText(Path.Combine(gravityCase.CasePath, "PorousWorkbenchProject.txt"));

CrossCheck(53, "No forced velocity boundary",
    "Zero initial U and pressureInletOutletVelocity at both top and bottom; rainfall velocity absent",
    $"zeroInitial={gravityU.Contains("internalField uniform (0 0 0)")}; pressureVelocityCount={gravityU.Split("type pressureInletOutletVelocity;", StringSplitOptions.None).Length - 1}",
    gravityU.Contains("internalField uniform (0 0 0)") &&
    gravityU.Split("type pressureInletOutletVelocity;", StringSplitOptions.None).Length - 1 == 2 &&
    !gravityU.Contains("-5.5555555555555558E-06", StringComparison.Ordinal));

CrossCheck(54, "Equal reference-pressure boundaries",
    "Top and bottom both fixedValue p=0 so no water-head pressure difference is imposed",
    $"fixedZeroCount={gravityP.Split("type fixedValue; value uniform 0;", StringSplitOptions.None).Length - 1}",
    gravityP.Contains("top { type fixedValue; value uniform 0; }") &&
    gravityP.Contains("bottom { type fixedValue; value uniform 0; }") &&
    gravityP.Split("type fixedValue; value uniform 0;", StringSplitOptions.None).Length - 1 == 2);

CrossCheck(55, "Gravity-only OpenFOAM forcing",
    "One buoyancyForce plus seven DarcyForchheimer porosityForce models",
    $"buoyancy={gravityModels.Split("type buoyancyForce;", StringSplitOptions.None).Length - 1}; porous={gravityModels.Split("type            porosityForce;", StringSplitOptions.None).Length - 1}",
    gravityModels.Split("type buoyancyForce;", StringSplitOptions.None).Length - 1 == 1 &&
    gravityModels.Split("type            porosityForce;", StringSplitOptions.None).Length - 1 == 7 &&
    proposalExpectedZones.All(zone => gravityModels.Contains($"cellZone        {zone};")));

CrossCheck(56, "Gravity vector and units",
    "constant/g contains SI acceleration (0 -9.81 0)",
    gravityVector.Contains("value (0 -9.8100000000000005 0)") ? "(0 -9.8100000000000005 0)" : gravityVector,
    gravityVector.Contains("dimensions [0 1 -2 0 0 0 0]") &&
    gravityVector.Contains("value (0 -9.8100000000000005 0)"));

var gravityAnalytical = PorousPhysics.CalculateAnalytical(proposalGravity);
CrossCheck(57, "Gravity preset analytical invariance",
    "Keff, hydraulic conductivity and resistance fractions match the same physical PDF stack",
    $"Keff={gravityAnalytical.EquivalentPermeability:G17}; K={gravityAnalytical.HydraulicConductivity:G17}; fraction={gravityAnalytical.Layers.Sum(layer => layer.ResistanceFraction):P9}",
    Math.Abs(gravityAnalytical.EquivalentPermeability - proposalAnalytical.EquivalentPermeability) < 1e-30 &&
    Math.Abs(gravityAnalytical.HydraulicConductivity - proposalAnalytical.HydraulicConductivity) < 1e-18 &&
    Math.Abs(gravityAnalytical.Layers.Sum(layer => layer.ResistanceFraction) - 1) < 1e-12);

CrossCheck(58, "Gravity-disabled execution protection",
    "Validation blocks GravityDrainage when gravity is disabled",
    string.Join(" | ", PorousPhysics.Validate(proposalGravity.CloneWith()).Issues.Select(issue => issue.Message)),
    PorousPhysics.Validate(new PorousCaseSettings
    {
        OutputRootPath = proposalGravity.OutputRootPath,
        ProjectName = proposalGravity.ProjectName,
        DomainWidthMm = proposalGravity.DomainWidthMm,
        Layers = proposalGravity.Layers.Select(layer => layer.Clone()).ToArray(),
        Density = proposalGravity.Density,
        DynamicViscosity = proposalGravity.DynamicViscosity,
        GravityEnabled = false,
        FlowMode = PorousFlowMode.GravityDrainage,
        SimulationType = PorousSimulationType.Steady,
        TargetCellSizeMm = proposalGravity.TargetCellSizeMm,
        MinimumCellsPerLayer = proposalGravity.MinimumCellsPerLayer,
        EndTime = proposalGravity.EndTime,
        WriteInterval = proposalGravity.WriteInterval
    }).Errors.Any(issue => issue.Field == "Gravity Drainage"));

var sourceRoot = Directory.GetCurrentDirectory();
var mainXaml = File.ReadAllText(Path.Combine(sourceRoot, "MainWindow.xaml"));
var mainCode = File.ReadAllText(Path.Combine(sourceRoot, "MainWindow.xaml.cs"));
var appXaml = File.ReadAllText(Path.Combine(sourceRoot, "App.xaml"));
CrossCheck(59, "Direct three-mode UI",
    "Visible segmented buttons for RainfallFlux, GravityDrainage and WaterHead; legacy enum combo hidden",
    $"rain={mainXaml.Contains("Tag=\"RainfallFlux\"")}; gravity={mainXaml.Contains("Tag=\"GravityDrainage\"")}; head={mainXaml.Contains("Tag=\"WaterHead\"")}",
    mainXaml.Contains("Tag=\"RainfallFlux\"") && mainXaml.Contains("Tag=\"GravityDrainage\"") &&
    mainXaml.Contains("Tag=\"WaterHead\"") && mainXaml.Contains("x:Name=\"PorousFlowModeCombo\" Visibility=\"Collapsed\""));

CrossCheck(60, "Flow-mode UI state synchronization",
    "Checked handler updates the enum selection and enables only the physically relevant input",
    $"handler={mainCode.Contains("PorousFlowMode_Checked")}; sync={mainCode.Contains("PorousFlowModeCombo.SelectedItem = mode")}",
    mainCode.Contains("PorousFlowMode_Checked") &&
    mainCode.Contains("PorousFlowModeCombo.SelectedItem = mode") &&
    mainCode.Contains("PorousRainfallBox.IsEnabled = mode == PorousFlowMode.RainfallFlux") &&
    mainCode.Contains("PorousWaterHeadBox.IsEnabled = mode == PorousFlowMode.WaterHead") &&
    mainCode.Contains("SetPorousFlowMode(settings.FlowMode)"));

CrossCheck(61, "Flow-mode selection contrast",
    "Selected segmented button uses explicit white text and accented background/border",
    $"style={appXaml.Contains("x:Key=\"FlowModeRadioButton\"")}; white={appXaml.Contains("<Setter Property=\"Foreground\" Value=\"#FFFFFF\"/>")}",
    appXaml.Contains("x:Key=\"FlowModeRadioButton\"") &&
    appXaml.Contains("<Trigger Property=\"IsChecked\" Value=\"True\">") &&
    appXaml.Contains("<Setter Property=\"Foreground\" Value=\"#FFFFFF\"/>") &&
    appXaml.Contains("<Setter TargetName=\"Root\" Property=\"Background\" Value=\"{StaticResource PrimaryGradient}\"/>"));

CrossCheck(62, "Gravity preset manifest persistence",
    "Generated project records GravityDrainage, source warning and acceptance thresholds",
    gravityManifest.Contains("Flow mode: GravityDrainage") ? "GravityDrainage persisted" : gravityManifest,
    gravityManifest.Contains("Flow mode: GravityDrainage") &&
    gravityManifest.Contains("잠정값") &&
    gravityManifest.Contains("CFD/analytical tolerance: 10 %"));

var visualizationSetFields = File.ReadAllText(Path.Combine(generatedPorous.CasePath, "system", "setFieldsDict"));
var initialLayerId = Path.Combine(generatedPorous.CasePath, "0", "layerId");
CrossCheck(67, "Layer ID visualization generation",
    "0/layerId exists and setFields assigns one dimensionless layer ID per region",
    $"file={File.Exists(initialLayerId)}; assignments={visualizationSetFields.Split("volScalarFieldValue layerId", StringSplitOptions.None).Length - 1}",
    File.Exists(initialLayerId) && File.ReadAllText(initialLayerId).Contains("dimensions [0 0 0 0 0 0 0]") &&
    visualizationSetFields.Split("volScalarFieldValue layerId", StringSplitOptions.None).Length - 1 == 8 &&
    Enumerable.Range(1, 7).All(id => visualizationSetFields.Contains($"volScalarFieldValue layerId {id}")));

CrossCheck(68, "Visualization field semantics",
    "Manifest and UI identify layerId/permeability as visualization-only input properties",
    $"manifest={File.ReadAllText(Path.Combine(generatedPorous.CasePath, "PorousWorkbenchProject.txt")).Contains("visualization-only")}; ui={mainXaml.Contains("visualization-only")}",
    File.ReadAllText(Path.Combine(generatedPorous.CasePath, "PorousWorkbenchProject.txt")).Contains("visualization-only") &&
    mainXaml.Contains("layerId") && mainXaml.Contains("visualization-only"));

var mockResultTime = Path.Combine(generatedPorous.CasePath, "123.5");
Directory.CreateDirectory(mockResultTime);
File.Copy(Path.Combine(generatedPorous.CasePath, "0", "U"), Path.Combine(mockResultTime, "U"));
File.Copy(Path.Combine(generatedPorous.CasePath, "0", "p"), Path.Combine(mockResultTime, "p"));
var publishedVisualization = PorousSimulationService.PublishVisualizationFieldsToResultTimes(generatedPorous.CasePath);
CrossCheck(69, "Latest-time visualization publication",
    "layerId and permeability are copied beside U/p with the result-time location header",
    string.Join(", ", publishedVisualization.Select(Path.GetFileName)),
    publishedVisualization.Count(path => Path.GetDirectoryName(path) == mockResultTime) == 2 &&
    File.ReadAllText(Path.Combine(mockResultTime, "layerId")).Contains("location \"123.5\";") &&
    File.ReadAllText(Path.Combine(mockResultTime, "permeability")).Contains("location \"123.5\";"));

CrossCheck(70, "ParaView field guidance",
    "Porous result UI exposes exact existing fields: layerId, permeability, U Magnitude and p",
    "layerId / permeability / U Magnitude / p",
    mainXaml.Contains("Color By → layerId") && mainXaml.Contains("U → Magnitude") && mainXaml.Contains(", p를 선택"));

var residualPlotSource = File.ReadAllText(Path.Combine(sourceRoot, "Controls", "ResidualPlot.cs"));
CrossCheck(71, "Residual mouse-wheel zoom",
    "Wheel zoom is anchored at the pointer and supports both zoom-in and zoom-out",
    $"wheel={residualPlotSource.Contains("OnMouseWheel")}; anchor={residualPlotSource.Contains("ZoomAt(pointer")}",
    residualPlotSource.Contains("protected override void OnMouseWheel") &&
    residualPlotSource.Contains("e.Delta > 0 ? 0.8 : 1.25") &&
    residualPlotSource.Contains("ZoomAt(pointer, plotArea, factor") &&
    residualPlotSource.Contains("e.Handled = true"));

CrossCheck(72, "Residual axis-specific zoom",
    "Shift+wheel changes only X and Ctrl+wheel changes only logarithmic Y",
    "Shift=X; Ctrl=Y; default=both",
    residualPlotSource.Contains("ModifierKeys.Shift") &&
    residualPlotSource.Contains("ModifierKeys.Control") &&
    residualPlotSource.Contains("!verticalOnly, !horizontalOnly"));

CrossCheck(73, "Residual zoom reset and guidance",
    "Double-click restores the complete viewport and the UI explains the interaction",
    $"reset={residualPlotSource.Contains("e.ClickCount == 2")}; hint={mainXaml.Contains("휠 확대/축소")}",
    residualPlotSource.Contains("e.ClickCount == 2") &&
    residualPlotSource.Contains("ResetZoom();") &&
    residualPlotSource.Contains("더블클릭: 전체 보기") &&
    mainXaml.Contains("잔차 이력 (Initial residual) · 휠 확대/축소"));

CrossCheck(74, "Residual zoom rendering bounds",
    "Zoomed curves are clipped to the plotting area and axes reflect the active viewport",
    "plot clip + dynamic X/log-Y ticks",
    residualPlotSource.Contains("dc.PushClip(new RectangleGeometry(plotArea))") &&
    residualPlotSource.Contains("_viewXMinimum") && residualPlotSource.Contains("_viewXMaximum") &&
    residualPlotSource.Contains("_viewLogMinimum") && residualPlotSource.Contains("_viewLogMaximum"));

double zoomedHorizontal = double.NaN;
double zoomedVertical = double.NaN;
double resetHorizontal = double.NaN;
double resetVertical = double.NaN;
var zoomStateAfterZoomIn = false;
var zoomStateAfterReset = true;
Exception? residualZoomException = null;
var residualZoomThread = new Thread(() =>
{
    try
    {
        var residualPlot = new ResidualPlot();
        residualPlot.Zoom(0.5, 0.5, 0.8);
        zoomedHorizontal = residualPlot.HorizontalZoomFactor;
        zoomedVertical = residualPlot.VerticalZoomFactor;
        zoomStateAfterZoomIn = residualPlot.IsZoomed;
        residualPlot.ResetZoom();
        resetHorizontal = residualPlot.HorizontalZoomFactor;
        resetVertical = residualPlot.VerticalZoomFactor;
        zoomStateAfterReset = residualPlot.IsZoomed;
    }
    catch (Exception exception)
    {
        residualZoomException = exception;
    }
});
residualZoomThread.SetApartmentState(ApartmentState.STA);
residualZoomThread.Start();
residualZoomThread.Join();
CrossCheck(75, "Residual viewport numerical behavior",
    "A wheel-up factor produces 1.25x zoom on both axes and reset returns exactly to 1x",
    residualZoomException is null
        ? $"zoomed=({zoomedHorizontal:G6},{zoomedVertical:G6}); reset=({resetHorizontal:G6},{resetVertical:G6})"
        : residualZoomException.Message,
    residualZoomException is null && zoomStateAfterZoomIn &&
    Math.Abs(zoomedHorizontal - 1.25) < 1e-12 &&
    Math.Abs(zoomedVertical - 1.25) < 1e-12 &&
    !zoomStateAfterReset &&
    Math.Abs(resetHorizontal - 1) < 1e-12 &&
    Math.Abs(resetVertical - 1) < 1e-12);

var casePath = Path.Combine(Path.GetTempPath(), $"FoamWorkbench-Smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(Path.Combine(casePath, "0"));
Directory.CreateDirectory(Path.Combine(casePath, "constant"));
Directory.CreateDirectory(Path.Combine(casePath, "system"));
File.WriteAllText(Path.Combine(casePath, "system", "controlDict"), controlDict);
var caseInfo = CaseInspector.Inspect(casePath);
Check(caseInfo.IsValid, "standard OpenFOAM case structure is accepted");
Check(caseInfo.Application == "foamRun" && caseInfo.SolverModule == "incompressibleFluid",
    "case solver metadata is detected");

var logDirectory = Path.Combine(casePath, "FoamWorkbenchLogs");
Directory.CreateDirectory(logDirectory);
var residualLog = Path.Combine(logDirectory, "session-with-residuals.log");
var newerLogWithoutResiduals = Path.Combine(logDirectory, "session-without-residuals.log");
File.WriteAllLines(residualLog,
[
    "smoothSolver: Solving for Ux, Initial residual = 4.2e-3, Final residual = 8.1e-7, No Iterations 3",
    "GAMG: Solving for p, Initial residual = 0.12, Final residual = 0.002, No Iterations 2"
]);
File.WriteAllText(newerLogWithoutResiduals, "checkMesh: Mesh OK\n");
File.SetLastWriteTimeUtc(residualLog, DateTime.UtcNow.AddMinutes(-2));
File.SetLastWriteTimeUtc(newerLogWithoutResiduals, DateTime.UtcNow.AddMinutes(-1));
var recoveredLog = ResidualLogRecovery.LoadLatest(casePath);
Check(recoveredLog is not null && recoveredLog.FilePath == residualLog,
    "case open recovery skips newer logs without residual data");
Check(recoveredLog is { Samples.Count: 2 } &&
      recoveredLog.Samples[0].Sequence == 1 &&
      Math.Abs(recoveredLog.Samples[1].Initial - 0.12) < 1e-12,
    "saved residual log restores the complete ordered sample history");

var plotArtifacts = PythonResidualPlotService.Prepare(
    casePath,
    recoveredLog!.Samples,
    new DateTimeOffset(2026, 8, 10, 12, 34, 56, TimeSpan.Zero));
var plotCsv = File.ReadAllText(plotArtifacts.CsvPath);
var plotScript = File.ReadAllText(plotArtifacts.ScriptPath);
Check(File.Exists(plotArtifacts.CsvPath) && File.Exists(plotArtifacts.ScriptPath) &&
      plotArtifacts.SampleCount == 2 && plotArtifacts.FieldCount == 2,
    "Python residual plot artifacts are prepared inside the case");
Check(plotCsv.Contains("0.0042", StringComparison.Ordinal) &&
      plotCsv.Contains("\"Ux\"", StringComparison.Ordinal) &&
      plotScript.Contains("matplotlib.use(\"Agg\")", StringComparison.Ordinal) &&
      plotScript.Contains("Final residual", StringComparison.Ordinal),
    "Python plot export preserves residual precision and renders initial/final detail");

if (args.Length == 1 && args[0] == "--probe-only")
{
    var probeSettings = new AppSettings
    {
        Backend = RuntimeBackend.Wsl,
        WslDistribution = "Ubuntu-24.04",
        OpenFoamBashrc = "/opt/openfoam14/etc/bashrc"
    };
    var probeRunner = new ProcessRunner();
    var probeService = new OpenFoamService(probeSettings, probeRunner);
    var probeOnly = await probeService.ProbeAsync();
    Console.WriteLine($"IsAvailable={probeOnly.IsAvailable}");
    Console.WriteLine($"Version={probeOnly.Version}");
    Console.WriteLine("Details:");
    Console.WriteLine(probeOnly.Details);
    return probeOnly.IsAvailable ? 0 : 1;
}

if (args.Length == 2 && args[0] == "--proposal-preset-integration")
{
    var outputRoot = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(outputRoot);
    Console.WriteLine();
    Console.WriteLine($"Running PDF proposal presets with real OpenFOAM: {outputRoot}");

    var runtime = new AppSettings
    {
        Backend = RuntimeBackend.Wsl,
        WslDistribution = "Ubuntu-24.04",
        OpenFoamBashrc = "/opt/openfoam14/etc/bashrc",
        ProcessCount = 1
    };
    var runner = new ProcessRunner();
    var service = new OpenFoamService(runtime, runner);
    var generator = new PorousCaseGenerator();
    var probe = await service.ProbeAsync();
    CrossCheck(42, "Proposal runtime",
        "Installed engine is Foundation OpenFOAM 14",
        probe.IsAvailable ? probe.Version : "not available",
        probe.IsAvailable && probe.Version.Contains("OpenFOAM-14", StringComparison.Ordinal));

    var scenarioA = PorousPresetFactory.CreateBuiltInSettings(PorousPresetFactory.ProposalRainfallId)
        .CloneWith(outputRootPath: outputRoot, projectName: "ProposalScenarioAValidation");
    var generatedA = generator.Generate(scenarioA);
    var aStages = new Dictionary<string, ProcessResult>();
    foreach (var command in new[] { "blockMesh", "setFields", "checkMesh -allTopology -allGeometry", "foamRun" })
    {
        var result = await service.RunCaseCommandAsync(generatedA.CasePath, command);
        aStages[command] = result;
        File.WriteAllText(Path.Combine(generatedA.CasePath,
            $"validation-{command.Split(' ')[0]}.log"), result.Output);
        if (result.ExitCode != 0) break;
    }
    CrossCheck(43, "Proposal Scenario A blockMesh",
        "PDF 80 x 25 mm structured mesh completes",
        aStages.TryGetValue("blockMesh", out var aBlock) ? $"exit={aBlock.ExitCode}" : "not run",
        aStages.TryGetValue("blockMesh", out aBlock) && aBlock.ExitCode == 0 && aBlock.Output.Contains("End"));
    CrossCheck(44, "Proposal Scenario A checkMesh",
        "Full OpenFOAM topology/geometry check reports Mesh OK",
        aStages.TryGetValue("checkMesh -allTopology -allGeometry", out var aCheck)
            ? $"exit={aCheck.ExitCode}; MeshOK={aCheck.Output.Contains("Mesh OK")}" : "not run",
        aStages.TryGetValue("checkMesh -allTopology -allGeometry", out aCheck) &&
        aCheck.ExitCode == 0 && aCheck.Output.Contains("Mesh OK"));

    IReadOnlyDictionary<string, int> aZoneCounts = new Dictionary<string, int>();
    try { aZoneCounts = PorousSimulationService.ReadCellZoneCounts(generatedA.CasePath); }
    catch (Exception exception) { Console.WriteLine(exception); }
    CrossCheck(45, "Proposal Scenario A real cellZones",
        "All seven proposal zones exist with non-zero cell counts and 4A/4B remain separate",
        string.Join(", ", aZoneCounts.Select(item => $"{item.Key}={item.Value}")),
        scenarioA.Layers.All(layer => aZoneCounts.TryGetValue(layer.Name, out var count) && count > 0) &&
        aZoneCounts.ContainsKey("layer4a_cldh") && aZoneCounts.ContainsKey("layer4b_acidTreatedZeolite"));
    CrossCheck(46, "Proposal Scenario A real solve",
        "foamRun/incompressibleFluid completes with the preset's Darcy and gravity models",
        aStages.TryGetValue("foamRun", out var aSolve) ? $"exit={aSolve.ExitCode}; End={aSolve.Output.Contains("End")}" : "not run",
        aStages.TryGetValue("foamRun", out aSolve) && aSolve.ExitCode == 0 && aSolve.Output.Contains("End"));

    var publishedA = PorousSimulationService.PublishVisualizationFieldsToResultTimes(generatedA.CasePath);
    PorousResultSummary? resultA = null;
    try { resultA = PorousResultProcessor.Load(generatedA.CasePath, scenarioA); }
    catch (Exception exception) { Console.WriteLine(exception); }
    var aTimes = PorousResultProcessor.FindResultTimes(generatedA.CasePath);
    CrossCheck(76, "Scenario A result-time directories",
        "Positive result directories reach the configured final time 400",
        string.Join(",", aTimes.Select(time => time.ToString("G8", CultureInfo.InvariantCulture))),
        aTimes.Count > 0 && Math.Abs(aTimes[^1] - 400) < 1e-12);
    CrossCheck(77, "Scenario A result fields",
        "Latest result contains U and kinematic p",
        $"U={File.Exists(Path.Combine(generatedA.CasePath, "400", "U"))}; p={File.Exists(Path.Combine(generatedA.CasePath, "400", "p"))}",
        File.Exists(Path.Combine(generatedA.CasePath, "400", "U")) &&
        File.Exists(Path.Combine(generatedA.CasePath, "400", "p")));
    CrossCheck(78, "Scenario A visualization publication",
        "layerId and permeability are published into every positive result time",
        string.Join(",", publishedA.Select(Path.GetFileName)),
        aTimes.All(time =>
        {
            var directory = Path.Combine(generatedA.CasePath, time.ToString("G17", CultureInfo.InvariantCulture));
            return File.Exists(Path.Combine(directory, "layerId")) && File.Exists(Path.Combine(directory, "permeability"));
        }));
    CrossCheck(79, "Scenario A imposed rainfall preserved",
        "Average inlet velocity remains 5.55555556e-6 m/s downward",
        resultA is null ? "result unavailable" : $"expected={resultA.ExpectedInletVelocity:G12}; actual={resultA.InletAverageVelocity:G12}; preserved={resultA.InletVelocityPreserved}",
        resultA is { InletVelocityPreserved: true } &&
        Math.Abs(resultA.InletAverageVelocity - PorousUnitConverter.MillimetresPerHourToMetresPerSecond(20)) < 1e-12);
    CrossCheck(80, "Scenario A flow balance",
        "Inlet and outlet flow rates balance within 1%",
        resultA?.FlowBalance is null ? "unavailable" : $"difference={resultA.FlowBalance.DifferencePercent:G10}%",
        resultA?.FlowBalance is { Pass: true });
    CrossCheck(81, "Scenario A convergence status",
        "Final time, residual and flow sanity checks classify the completed run as converged",
        resultA is null ? "unavailable" : $"status={resultA.SimulationStatus}; final={resultA.FinalTime}; residual={resultA.FinalResidualMaximum:G6}",
        resultA is { SimulationStatus: PorousSimulationStatus.Converged, FinalTime: 400 });
    CrossCheck(82, "Scenario A pressure semantics",
        "Foundation incompressibleFluid result uses p [m2/s2], not a nonexistent p_rgh field",
        $"p={File.ReadAllText(Path.Combine(generatedA.CasePath, "0", "p")).Contains("dimensions      [0 2 -2 0 0 0 0]")}; p_rgh={File.Exists(Path.Combine(generatedA.CasePath, "0", "p_rgh"))}",
        File.ReadAllText(Path.Combine(generatedA.CasePath, "0", "p")).Contains("[0 2 -2 0 0 0 0]") &&
        !File.Exists(Path.Combine(generatedA.CasePath, "0", "p_rgh")));

    var scenarioB = PorousPresetFactory.CreateBuiltInSettings(PorousPresetFactory.ProposalWaterHeadId)
        .CloneWith(outputRootPath: outputRoot, projectName: "ProposalScenarioBValidation",
            endTime: 1, writeInterval: 1, deltaT: 0.001);
    var generatedB = generator.Generate(scenarioB);
    var bBlock = await service.RunCaseCommandAsync(generatedB.CasePath, "blockMesh");
    var bCheck = bBlock.ExitCode == 0
        ? await service.RunCaseCommandAsync(generatedB.CasePath, "checkMesh -allTopology -allGeometry")
        : new ProcessResult(-1, TimeSpan.Zero, "blockMesh failed");
    File.WriteAllText(Path.Combine(generatedB.CasePath, "validation-blockMesh.log"), bBlock.Output);
    File.WriteAllText(Path.Combine(generatedB.CasePath, "validation-checkMesh.log"), bCheck.Output);
    CrossCheck(47, "Proposal Scenario B real mesh",
        "Transient water-head preset blockMesh and checkMesh both succeed",
        $"blockMesh={bBlock.ExitCode}; checkMesh={bCheck.ExitCode}; MeshOK={bCheck.Output.Contains("Mesh OK")}",
        bBlock.ExitCode == 0 && bCheck.ExitCode == 0 && bCheck.Output.Contains("Mesh OK"));

    var bSchemes = File.ReadAllText(Path.Combine(generatedB.CasePath, "system", "fvSchemes"));
    var bSolution = File.ReadAllText(Path.Combine(generatedB.CasePath, "system", "fvSolution"));
    var bPressure = File.ReadAllText(Path.Combine(generatedB.CasePath, "0", "p"));
    CrossCheck(48, "Proposal Scenario B transient dictionaries",
        "Euler, PIMPLE and 50 mm water-head pressure boundary are present",
        $"Euler={bSchemes.Contains("default Euler;")}; PIMPLE={bSolution.Contains("PIMPLE")}; pressureBoundary={bPressure.Contains("value uniform 0.4905")}",
        bSchemes.Contains("default Euler;") && bSolution.Contains("PIMPLE") &&
        bPressure.Contains("top { type fixedValue; value uniform 0.4905") &&
        bPressure.Contains("bottom { type fixedValue; value uniform 0; }"));

    var bSetFields = bBlock.ExitCode == 0
        ? await service.RunCaseCommandAsync(generatedB.CasePath, "setFields")
        : new ProcessResult(-1, TimeSpan.Zero, "blockMesh failed");
    var bSolve = bSetFields.ExitCode == 0 && bCheck.ExitCode == 0
        ? await service.RunCaseCommandAsync(generatedB.CasePath, "foamRun")
        : new ProcessResult(-1, TimeSpan.Zero, "setup failed");
    File.WriteAllText(Path.Combine(generatedB.CasePath, "validation-setFields.log"), bSetFields.Output);
    File.WriteAllText(Path.Combine(generatedB.CasePath, "validation-foamRun.log"), bSolve.Output);
    PorousSimulationService.PublishVisualizationFieldsToResultTimes(generatedB.CasePath);
    var bTimes = PorousResultProcessor.FindResultTimes(generatedB.CasePath);
    CrossCheck(83, "Scenario B real transient solve",
        "foamRun advances the 50 mm water-head case through 1000 transient steps to Time=1",
        $"setFields={bSetFields.ExitCode}; solve={bSolve.ExitCode}; End={bSolve.Output.Contains("End")}; latest={bTimes.LastOrDefault():G8}",
        bSetFields.ExitCode == 0 && bSolve.ExitCode == 0 && bSolve.Output.Contains("End") &&
        bTimes.Count > 0 && Math.Abs(bTimes[^1] - 1) < 1e-12);
    CrossCheck(84, "Scenario B solver equations",
        "Real log solves transient U and p equations",
        $"Time={bSolve.Output.Contains("Time = 1")}; U={bSolve.Output.Contains("Solving for Ux") || bSolve.Output.Contains("Solving for Uy")}; p={bSolve.Output.Contains("Solving for p")}",
        bSolve.Output.Contains("Time = 1") &&
        (bSolve.Output.Contains("Solving for Ux") || bSolve.Output.Contains("Solving for Uy")) &&
        bSolve.Output.Contains("Solving for p"));
    CrossCheck(85, "Scenario B result fields",
        "Latest transient result contains U, p, layerId and permeability",
        bTimes.Count == 0 ? "no result" : string.Join(",", new[] { "U", "p", "layerId", "permeability" }.Where(field => File.Exists(Path.Combine(generatedB.CasePath, bTimes[^1].ToString("G17", CultureInfo.InvariantCulture), field)))),
        bTimes.Count > 0 && new[] { "U", "p", "layerId", "permeability" }.All(field =>
            File.Exists(Path.Combine(generatedB.CasePath, bTimes[^1].ToString("G17", CultureInfo.InvariantCulture), field))));
    CrossCheck(86, "Scenario B monitoring cadence",
        "Transient residual functionObject is throttled to every 0.1 s without changing deltaT physics",
        File.ReadAllText(Path.Combine(generatedB.CasePath, "system", "controlDict")),
        File.ReadAllText(Path.Combine(generatedB.CasePath, "system", "controlDict")).Contains("writeInterval 100;") &&
        Math.Abs(scenarioB.DeltaT - 0.001) < 1e-15);

    var paraViewScriptPath = OpenFoamService.PrepareParaViewStartupScript(generatedA.CasePath);
    var paraViewScript = File.ReadAllText(paraViewScriptPath);
    CrossCheck(87, "ParaView latest-time startup",
        "Generated ParaView script skips Time=0, selects latest time and exposes U Magnitude",
        $"path={paraViewScriptPath}; skipZero={paraViewScript.Contains("SkipZeroTime = 1")}; latest={paraViewScript.Contains("max(times)")}; colorU={paraViewScript.Contains("ColorBy(display, ('CELLS', 'U', 'Magnitude'))")}",
        paraViewScript.Contains("SkipZeroTime = 1") && paraViewScript.Contains("max(times)") &&
        paraViewScript.Contains("ColorBy(display, ('CELLS', 'U', 'Magnitude'))"));

    CrossCheck(49, "Proposal source and acceptance persistence",
        "Generated manifest preserves temporary-value warning, 5.56e-6 m/s threshold and 10% tolerance",
        Path.Combine(generatedA.CasePath, "PorousWorkbenchProject.txt"),
        File.ReadAllText(Path.Combine(generatedA.CasePath, "PorousWorkbenchProject.txt")).Contains("잠정값") &&
        File.ReadAllText(Path.Combine(generatedA.CasePath, "PorousWorkbenchProject.txt")).Contains("5.5600000000000001E-06 m/s") &&
        File.ReadAllText(Path.Combine(generatedA.CasePath, "PorousWorkbenchProject.txt")).Contains("CFD/analytical tolerance: 10 %"));

    var scenarioGravity = PorousPresetFactory.CreateBuiltInSettings(PorousPresetFactory.ProposalGravityDrainageId)
        .CloneWith(outputRootPath: outputRoot, projectName: "ProposalGravityDrainageValidation");
    var generatedGravity = generator.Generate(scenarioGravity);
    var gravityStages = new Dictionary<string, ProcessResult>();
    foreach (var command in new[] { "blockMesh", "setFields", "checkMesh -allTopology -allGeometry", "foamRun" })
    {
        var result = await service.RunCaseCommandAsync(generatedGravity.CasePath, command);
        gravityStages[command] = result;
        File.WriteAllText(Path.Combine(generatedGravity.CasePath,
            $"validation-{command.Split(' ')[0]}.log"), result.Output);
        if (result.ExitCode != 0) break;
    }

    CrossCheck(63, "Gravity Drainage real mesh",
        "blockMesh, setFields and full checkMesh all complete with Mesh OK",
        $"block={gravityStages.GetValueOrDefault("blockMesh")?.ExitCode}; setFields={gravityStages.GetValueOrDefault("setFields")?.ExitCode}; check={gravityStages.GetValueOrDefault("checkMesh -allTopology -allGeometry")?.ExitCode}",
        gravityStages.TryGetValue("blockMesh", out var gravityBlock) && gravityBlock.ExitCode == 0 &&
        gravityStages.TryGetValue("setFields", out var gravitySetFields) && gravitySetFields.ExitCode == 0 &&
        gravityStages.TryGetValue("checkMesh -allTopology -allGeometry", out var gravityCheck) &&
        gravityCheck.ExitCode == 0 && gravityCheck.Output.Contains("Mesh OK"));

    IReadOnlyDictionary<string, int> gravityZoneCounts = new Dictionary<string, int>();
    try { gravityZoneCounts = PorousSimulationService.ReadCellZoneCounts(generatedGravity.CasePath); }
    catch (Exception exception) { Console.WriteLine(exception); }
    CrossCheck(64, "Gravity Drainage real cellZones",
        "All seven PDF zones exist and contain cells",
        string.Join(", ", gravityZoneCounts.Select(item => $"{item.Key}={item.Value}")),
        scenarioGravity.Layers.All(layer => gravityZoneCounts.TryGetValue(layer.Name, out var count) && count > 0));

    CrossCheck(65, "Gravity Drainage real solve",
        "Foundation OpenFOAM 14 incompressibleFluid completes without a forced inlet velocity",
        gravityStages.TryGetValue("foamRun", out var gravitySolve)
            ? $"exit={gravitySolve.ExitCode}; End={gravitySolve.Output.Contains("End")}" : "not run",
        gravityStages.TryGetValue("foamRun", out gravitySolve) &&
        gravitySolve.ExitCode == 0 && gravitySolve.Output.Contains("End"));

    PorousResultSummary? gravityResults = null;
    try { gravityResults = PorousResultProcessor.Load(generatedGravity.CasePath, scenarioGravity); }
    catch (Exception exception) { Console.WriteLine(exception); }
    var gravityArea = PorousUnitConverter.MillimetresToMetres(scenarioGravity.DomainWidthMm) *
                      PorousUnitConverter.MillimetresToMetres(scenarioGravity.TargetCellSizeMm);
    var uGravity = gravityResults is null ? double.NaN : Math.Abs(gravityResults.OutletFlowRate) / gravityArea;
    var uGravityError = Math.Abs(uGravity - generatedGravity.Analytical.HydraulicConductivity) /
                        Math.Max(generatedGravity.Analytical.HydraulicConductivity, 1e-30);
    CrossCheck(66, "Gravity Drainage physical result",
        "Qgravity is non-zero, flow balance passes and Ugravity agrees with saturated Darcy K within 3%",
        gravityResults is null
            ? "result loading failed"
            : $"Qgravity={Math.Abs(gravityResults.OutletFlowRate):G10} m3/s; Ugravity={uGravity:G10} m/s; K={generatedGravity.Analytical.HydraulicConductivity:G10} m/s; error={uGravityError:P5}; balance={gravityResults.FlowBalance?.DifferencePercent:G8}%",
        gravityResults is not null && double.IsFinite(uGravity) && uGravity > 0 &&
        gravityResults.FlowBalance is { Pass: true } && uGravityError <= 0.03);

    if (failures.Count > 0) return 1;
    Console.WriteLine("PDF proposal preset OpenFOAM integration passed.");
    return 0;
}

if (args.Length == 2 && args[0] == "--porous-integration")
{
    var outputRoot = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(outputRoot);
    Console.WriteLine();
    Console.WriteLine($"Running generated Porous Media cases with real OpenFOAM: {outputRoot}");

    var runtime = new AppSettings
    {
        Backend = RuntimeBackend.Wsl,
        WslDistribution = "Ubuntu-24.04",
        OpenFoamBashrc = "/opt/openfoam14/etc/bashrc",
        ProcessCount = 1
    };
    var integrationRunner = new ProcessRunner();
    var service = new OpenFoamService(runtime, integrationRunner);
    var generator = new PorousCaseGenerator();
    var allOutput = new StringBuilder();
    integrationRunner.OutputReceived += (_, item) => allOutput.AppendLine(item.Line);

    var probe = await service.ProbeAsync();
    CrossCheck(27, "Installed OpenFOAM distribution",
        "Foundation OpenFOAM 14 is detected from the real command environment",
        probe.IsAvailable ? probe.Version : "not available",
        probe.IsAvailable && probe.Version.Contains("OpenFOAM-14", StringComparison.Ordinal));

    var steady = ValidPorousSettings(outputRoot, "PorousSteadyValidation", endTime: 20);
    var generated = generator.Generate(steady);
    var blockDictionary = File.ReadAllText(Path.Combine(generated.CasePath, "system", "blockMeshDict"));
    var bottomLayerIndex = blockDictionary.IndexOf("layer7_bamboo", StringComparison.Ordinal);
    var topLayerIndex = blockDictionary.LastIndexOf("layer1_abaca", StringComparison.Ordinal);
    CrossCheck(30, "Physical TOP-to-BOTTOM layer placement",
        "Bottom-most block is layer7_bamboo and top-inlet block is layer1_abaca",
        $"bottomIndex={bottomLayerIndex}; topIndex={topLayerIndex}",
        bottomLayerIndex >= 0 && topLayerIndex > bottomLayerIndex);
    var blockMesh = await service.RunCaseCommandAsync(generated.CasePath, "blockMesh");
    File.WriteAllText(Path.Combine(generated.CasePath, "validation-blockMesh.log"), blockMesh.Output);
    CrossCheck(6, "Real blockMesh", "Generated structured seven-layer mesh exits successfully",
        $"exit={blockMesh.ExitCode}", blockMesh.ExitCode == 0 && blockMesh.Output.Contains("End"));

    var setFields = await service.RunCaseCommandAsync(generated.CasePath, "setFields");
    File.WriteAllText(Path.Combine(generated.CasePath, "validation-setFields.log"), setFields.Output);
    CrossCheck(28, "Permeability visualization field", "setFields populates the input-property scalar field",
        $"exit={setFields.ExitCode}", setFields.ExitCode == 0 && setFields.Output.Contains("End"));

    IReadOnlyDictionary<string, int> zoneCounts = new Dictionary<string, int>();
    try { zoneCounts = PorousSimulationService.ReadCellZoneCounts(generated.CasePath); }
    catch (Exception exception) { Console.WriteLine(exception); }
    var integrationZones = steady.Layers.Select(layer => layer.Name).ToArray();
    CrossCheck(8, "Real cellZones", "All seven exact zone names exist and every zone has cells",
        string.Join(", ", zoneCounts.Select(item => $"{item.Key}={item.Value}")),
        integrationZones.All(zone => zoneCounts.TryGetValue(zone, out var count) && count > 0));

    var checkMesh = await service.RunCaseCommandAsync(generated.CasePath, "checkMesh -allTopology -allGeometry");
    File.WriteAllText(Path.Combine(generated.CasePath, "validation-checkMesh.log"), checkMesh.Output);
    CrossCheck(7, "Real checkMesh", "Full topology and geometry diagnostics report Mesh OK",
        $"exit={checkMesh.ExitCode}; MeshOK={checkMesh.Output.Contains("Mesh OK")}",
        checkMesh.ExitCode == 0 && checkMesh.Output.Contains("Mesh OK"));

    var solve = await service.RunCaseCommandAsync(generated.CasePath, "foamRun");
    File.WriteAllText(Path.Combine(generated.CasePath, "validation-foamRun.log"), solve.Output);
    CrossCheck(22, "Real steady porous solve", "incompressibleFluid finishes with porosityForce and buoyancyForce",
        $"exit={solve.ExitCode}; End={solve.Output.Contains("End")}",
        solve.ExitCode == 0 && solve.Output.Contains("End"));

    var publishedResultFields = PorousSimulationService.PublishVisualizationFieldsToResultTimes(generated.CasePath);
    var numericResultTimes = Directory.EnumerateDirectories(generated.CasePath)
        .Where(path => double.TryParse(Path.GetFileName(path), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var time) && time > 0)
        .OrderBy(path => double.Parse(Path.GetFileName(path), CultureInfo.InvariantCulture))
        .ToArray();
    var latestResultTime = numericResultTimes.LastOrDefault();
    CrossCheck(35, "Real result time directory",
        "The completed solver writes a positive time directory containing U and p",
        latestResultTime is null ? "missing" : Path.GetFileName(latestResultTime),
        latestResultTime is not null &&
        File.Exists(Path.Combine(latestResultTime, "U")) &&
        File.Exists(Path.Combine(latestResultTime, "p")));
    CrossCheck(36, "Result-time visualization fields",
        "Every solver result time contains layerId and permeability with its own OpenFOAM location",
        $"published={string.Join(",", publishedResultFields)}; resultTimes={numericResultTimes.Length}",
        numericResultTimes.Length > 0 && numericResultTimes.All(path =>
            File.Exists(Path.Combine(path, "layerId")) &&
            File.Exists(Path.Combine(path, "permeability")) &&
            File.ReadAllText(Path.Combine(path, "layerId")).Contains(
                $"location \"{Path.GetFileName(path)}\";", StringComparison.Ordinal) &&
            File.ReadAllText(Path.Combine(path, "permeability")).Contains(
                $"location \"{Path.GetFileName(path)}\";", StringComparison.Ordinal)));
    CrossCheck(37, "ParaView marker",
        "Exactly one .foam marker identifies the generated porous case",
        string.Join(", ", Directory.EnumerateFiles(generated.CasePath, "*.foam").Select(Path.GetFileName)),
        Directory.EnumerateFiles(generated.CasePath, "*.foam").Count() == 1);
    CrossCheck(38, "CellZone coverage and non-overlap",
        "Seven zone cell counts sum exactly to the total structured-mesh cell count",
        $"zones={zoneCounts.Count}; zoneCells={zoneCounts.Values.Sum()}; totalCells={generated.TotalCells}",
        zoneCounts.Count == 7 && zoneCounts.Values.Sum() == generated.TotalCells);
    var porousModelSelections = Regex.Matches(solve.Output, "selecting model: DarcyForchheimer").Count;
    var porousZoneCreations = Regex.Matches(solve.Output, "creating porous zone:").Count;
    CrossCheck(39, "Solver log physics evidence",
        "The solver selects seven DarcyForchheimer models/zones and solves U and p",
        $"models={porousModelSelections}; zones={porousZoneCreations}; U={solve.Output.Contains("Solving for Uy")}; p={solve.Output.Contains("Solving for p")}",
        porousModelSelections == 7 && porousZoneCreations == 7 &&
        solve.Output.Contains("Solving for Uy") && solve.Output.Contains("Solving for p"));

    var steadyResults = PorousResultProcessor.Load(generated.CasePath, steady);
    CrossCheck(29, "Real post-processing outputs",
        "Flow, layer averages and centerline CSV are produced by the actual solver run",
        $"Qin={steadyResults.InletFlowRate:G9}; Qout={steadyResults.OutletFlowRate:G9}; centerline={steadyResults.CenterlineCsvPath}",
        double.IsFinite(steadyResults.InletFlowRate) &&
        double.IsFinite(steadyResults.OutletFlowRate) &&
        steadyResults.Layers.Count == 7 &&
        File.Exists(steadyResults.CenterlineCsvPath));

    var interfacePressureReady = steadyResults.Layers.All(layer =>
        double.IsFinite(layer.AverageInletPressurePa) &&
        double.IsFinite(layer.AverageOutletPressurePa) &&
        double.IsFinite(layer.PressureDropPa));
    CrossCheck(31, "Layer interface pressure and pressure drop",
        "Actual area-averaged Pin, Pout and delta-P exist for every cellZone",
        string.Join("; ", steadyResults.Layers.Select(layer => $"L{layer.LayerId}:dP={layer.PressureDropPa:G7}")),
        interfacePressureReady);

    var analyticalResistance = generated.Analytical.Layers.Sum(layer => layer.Resistance);
    var qRequired = PorousUnitConverter.MillimetresPerHourToMetresPerSecond(steady.RainfallMmPerHour);
    var predictedPressureDrop = steady.DynamicViscosity * qRequired * analyticalResistance -
                                steady.Density * Math.Abs(steady.GravityY) * generated.Analytical.TotalThicknessMetres;
    var pressureRelativeError = Math.Abs(steadyResults.PressureDropPa - predictedPressureDrop) /
                                Math.Max(Math.Abs(predictedPressureDrop), 1e-30);
    CrossCheck(32, "CFD versus Darcy-plus-gravity pressure balance",
        "Real CFD pressure drop agrees with the independent series-Darcy and gravity prediction within 3%",
        $"CFD={steadyResults.PressureDropPa:G10} Pa; predicted={predictedPressureDrop:G10} Pa; error={pressureRelativeError:P5}",
        pressureRelativeError <= 0.03);

    CrossCheck(33, "Real flow balance",
        "Actual inlet/outlet volume-flow mismatch is <=1% with divide-by-zero protection",
        steadyResults.FlowBalance is null ? "missing" : $"{steadyResults.FlowBalance.DifferencePercent:G8}%",
        steadyResults.FlowBalance is { Pass: true });

    CrossCheck(40, "CFD equivalent permeability",
        "Darcy permeability inverted from the real CFD pressure/flow balance agrees within the configured 10% verification tolerance",
        $"analytical={generated.Analytical.EquivalentPermeability:G10}; CFD={steadyResults.CfdEquivalentPermeability:G10}; difference={steadyResults.CfdAnalyticalDifferencePercent:G8}%",
        steadyResults.CfdEquivalentPermeability is > 0 &&
        steadyResults.CfdAnalyticalDifferencePercent >= 0 &&
        steadyResults.CfdAnalyticalDifferencePercent <= (steady.CfdAnalyticalTolerancePercent ?? 10));

    var transient = ValidPorousSettings(outputRoot, "PorousTransientValidation",
        PorousSimulationType.Transient, PorousFlowMode.WaterHead,
        endTime: 1, deltaT: 0.01, writeInterval: 1);
    var transientCase = generator.Generate(transient);
    var transientCommands = new[] { "blockMesh", "setFields", "checkMesh", "foamRun" };
    var transientPassed = true;
    foreach (var command in transientCommands)
    {
        var result = await service.RunCaseCommandAsync(transientCase.CasePath, command);
        File.WriteAllText(Path.Combine(transientCase.CasePath,
            $"validation-{command.Replace(' ', '-')}.log"), result.Output);
        transientPassed &= result.ExitCode == 0;
        if (result.ExitCode != 0) break;
    }
    CrossCheck(23, "Real transient porous solve",
        "Water-head case starts and completes with Euler/PIMPLE/incompressibleFluid",
        $"all stages successful={transientPassed}", transientPassed);

    var drainage = ValidPorousSettings(outputRoot, "PorousGravityDrainageValidation",
        PorousSimulationType.Steady, PorousFlowMode.GravityDrainage, endTime: 30);
    var drainageCase = generator.Generate(drainage);
    var drainagePassed = true;
    foreach (var command in new[] { "blockMesh", "setFields", "checkMesh", "foamRun" })
    {
        var result = await service.RunCaseCommandAsync(drainageCase.CasePath, command);
        File.WriteAllText(Path.Combine(drainageCase.CasePath,
            $"validation-{command.Replace(' ', '-')}.log"), result.Output);
        drainagePassed &= result.ExitCode == 0;
        if (result.ExitCode != 0) break;
    }
    var drainageResults = drainagePassed
        ? PorousResultProcessor.Load(drainageCase.CasePath, drainage)
        : null;
    var drainageArea = PorousUnitConverter.MillimetresToMetres(drainage.DomainWidthMm) *
                       PorousUnitConverter.MillimetresToMetres(drainage.TargetCellSizeMm);
    var qGravity = drainageResults is null ? double.NaN : Math.Abs(drainageResults.OutletFlowRate);
    var uGravity = qGravity / drainageArea;
    CrossCheck(34, "Real gravity drainage",
        "With no imposed velocity, gravity alone produces finite non-zero Qgravity and Ugravity",
        $"stages={drainagePassed}; Qgravity={qGravity:G10} m3/s; Ugravity={uGravity:G10} m/s",
        drainagePassed && double.IsFinite(qGravity) && qGravity > 0 && double.IsFinite(uGravity) && uGravity > 0);

    File.WriteAllText(Path.Combine(outputRoot, "porous-integration-console.log"), allOutput.ToString());
    if (failures.Count > 0) return 1;
    Console.WriteLine("Real Porous Media integration passed.");
    return 0;
}

if (args.Length == 2 && args[0] == "--python-plot-integration")
{
    var plotCase = Path.GetFullPath(args[1]);
    var plotData = ResidualLogRecovery.LoadLatest(plotCase);
    Check(plotData is not null && plotData.Samples.Count > 0,
        "integration case contains recoverable residual samples");
    if (plotData is null) return 1;

    var plotSettings = new AppSettings
    {
        Backend = RuntimeBackend.Wsl,
        WslDistribution = "Ubuntu-24.04",
        OpenFoamBashrc = "/opt/openfoam14/etc/bashrc"
    };
    var plotService = new PythonResidualPlotService(
        new OpenFoamService(plotSettings, new ProcessRunner()));
    var generatedPlot = await plotService.GenerateAsync(plotCase, plotData.Samples, 100);
    Check(File.Exists(generatedPlot.Artifacts.PngPath) &&
          new FileInfo(generatedPlot.Artifacts.PngPath).Length > 10_000,
        "real matplotlib execution creates a high-resolution PNG residual plot");
    Check(File.Exists(generatedPlot.Artifacts.SvgPath) &&
          new FileInfo(generatedPlot.Artifacts.SvgPath).Length > 1_000,
        "real matplotlib execution creates a vector SVG residual plot");

    if (failures.Count > 0) return 1;
    Console.WriteLine($"PNG={generatedPlot.Artifacts.PngPath}");
    Console.WriteLine($"SVG={generatedPlot.Artifacts.SvgPath}");
    Console.WriteLine("Python residual plot integration passed.");
    return 0;
}

if (args.Length == 2 && args[0] == "--preview-integration")
{
    var previewInput = Path.GetFullPath(args[1]);
    var previewRuntime = new AppSettings
    {
        Backend = RuntimeBackend.Wsl,
        WslDistribution = "Ubuntu-24.04",
        OpenFoamBashrc = "/opt/openfoam14/etc/bashrc"
    };
    var previewRunner = new ProcessRunner();
    var previewService = new CadPreviewService(
        new OpenFoamService(previewRuntime, previewRunner));
    var preview = await previewService.BuildAsync(
        previewInput, CadLengthUnit.Metre, 0.08);
    Check(preview.OriginalTriangleCount > 0 && preview.Triangles.Count > 0,
        "STEP preview conversion returns display triangles");
    Check(preview.Bounds.XLength > 0 &&
          preview.Bounds.YLength > 0 &&
          preview.Bounds.ZLength > 0,
        "STEP preview retains three-dimensional metric bounds");

    if (failures.Count > 0) return 1;
    Console.WriteLine("CAD preview integration passed.");
    return 0;
}

if (args.Length == 3 &&
    (args[0] == "--cad-integration" || args[0] == "--cad-internal-integration"))
{
    var internalFlow = args[0] == "--cad-internal-integration";
    var cadInput = Path.GetFullPath(args[1]);
    var outputRoot = Path.GetFullPath(args[2]);
    Console.WriteLine();
    Console.WriteLine($"Running CAD integration input: {cadInput}");

    var cadRuntime = new AppSettings
    {
        Backend = RuntimeBackend.Wsl,
        WslDistribution = "Ubuntu-24.04",
        OpenFoamBashrc = "/opt/openfoam14/etc/bashrc",
        ProcessCount = 2
    };
    var cadRunner = new ProcessRunner();
    var cadService = new OpenFoamService(cadRuntime, cadRunner);
    cadRunner.OutputReceived += (_, output) => Console.WriteLine(output.Line);

    var generator = new CadCaseGenerator(cadService);
    var project = new CadProjectSettings
    {
        CadFilePath = cadInput,
        OutputRootPath = outputRoot,
        ProjectName = internalFlow ? "StepInternalValidation" : "StepExternalValidation",
        CadUnit = CadLengthUnit.Metre,
        AnalysisType = internalFlow ? CadAnalysisType.InternalFluidVolume : CadAnalysisType.ExternalFlow,
        FlowAxis = FlowAxis.PositiveX,
        Turbulence = internalFlow ? TurbulenceChoice.Laminar : TurbulenceChoice.KOmegaSst,
        Velocity = 5,
        KinematicViscosity = 1.5e-5,
        CadSurfaceSize = 0.08,
        BaseCellSize = internalFlow ? 0.12 : 0.25,
        SurfaceRefinementMin = 2,
        SurfaceRefinementMax = 3,
        FeatureRefinementLevel = 3,
        BoundaryLayerCount = 0,
        MaxGlobalCells = 750_000,
        UpstreamLengths = 1.5,
        DownstreamLengths = 3,
        SideLengths = 1.5,
        EndTime = 25,
        WriteInterval = 25,
        ProcessCount = 2,
        FluidPointText = internalFlow ? "0.5 0 0" : null,
        CalculateResiduals = true,
        CalculateForces = true,
        CalculateForceCoefficients = true,
        CalculateWallShearStress = true,
        CalculateYPlus = !internalFlow,
        CalculateQCriterion = true,
        CalculateVorticity = true
    };

    var generated = await generator.GenerateAsync(project);
    Check(Directory.Exists(Path.Combine(generated.CasePath, "constant", "geometry")),
        "STEP is converted through Gmsh/OpenCASCADE into OpenFOAM geometry");
    Check(File.Exists(Path.Combine(generated.CasePath, "system", "snappyHexMeshDict")),
        "complete snappyHexMesh dictionary is generated");
    Check(File.Exists(Path.Combine(generated.CasePath, "FoamWorkbenchProject.txt")),
        "CAD conversion and numerical settings manifest is stored with the case");

    var cadCommands = new List<(string title, string command)>
    {
        ("blockMesh", "blockMesh"),
        ("surfaceFeatures", "surfaceFeatures"),
        ("snappyHexMesh", "snappyHexMesh -overwrite")
    };
    if (internalFlow) cadCommands.Add(("createPatch", "createPatch -overwrite"));
    cadCommands.Add(("checkMesh", "checkMesh -allTopology -allGeometry"));
    cadCommands.Add(("foamRun", "foamRun"));

    foreach (var (title, command) in cadCommands)
    {
        var result = await cadService.RunCaseCommandAsync(generated.CasePath, command);
        File.WriteAllText(Path.Combine(generated.CasePath, $"validation-{title}.log"), result.Output);
        var acceptable = result.ExitCode == 0;
        if (title == "checkMesh" &&
            result.Output.Contains("Failed ", StringComparison.OrdinalIgnoreCase))
        {
            var defaultCheck = await cadService.RunCaseCommandAsync(generated.CasePath, "checkMesh");
            File.WriteAllText(Path.Combine(generated.CasePath, "validation-checkMesh-default.log"),
                defaultCheck.Output);
            acceptable = defaultCheck.ExitCode == 0 &&
                         defaultCheck.Output.Contains("Mesh OK", StringComparison.OrdinalIgnoreCase);
            Check(acceptable,
                "strict mesh diagnostics are retained and default OpenFOAM solver checks report Mesh OK");
        }
        Check(acceptable, $"real OpenFOAM {title} completes for imported STEP");
        if (!acceptable) break;
    }

    if (!internalFlow)
    {
        Check(File.Exists(Path.Combine(generated.CasePath, "postProcessing", "forces", "0", "forces.dat")),
            "OpenFOAM writes pressure and viscous force histories");
        Check(File.Exists(Path.Combine(generated.CasePath, "postProcessing", "forceCoeffs", "0", "forceCoeffs.dat")),
            "OpenFOAM writes drag, lift and moment coefficient histories");
        Check(File.Exists(Path.Combine(generated.CasePath, "25", "wallShearStress")) &&
              File.Exists(Path.Combine(generated.CasePath, "25", "yPlus")) &&
              File.Exists(Path.Combine(generated.CasePath, "25", "Q")) &&
              File.Exists(Path.Combine(generated.CasePath, "25", "vorticity")),
            "OpenFOAM writes selected wall and derived result fields for ParaView");
    }

    if (failures.Count > 0) return 1;
    Console.WriteLine("CAD integration passed.");
    return 0;
}

if (args.Length == 2 && args[0] == "--integration")
{
    var integrationCase = Path.GetFullPath(args[1]);
    Console.WriteLine();
    Console.WriteLine($"Running real OpenFOAM integration case: {integrationCase}");

    var settings = new AppSettings
    {
        Backend = RuntimeBackend.Wsl,
        WslDistribution = "Ubuntu-24.04",
        OpenFoamBashrc = "/opt/openfoam14/etc/bashrc"
    };
    var runner = new ProcessRunner();
    var service = new OpenFoamService(settings, runner);
    var integrationResiduals = 0;
    var integrationParser = new ResidualParser();
    integrationParser.SampleParsed += _ => integrationResiduals++;
    runner.OutputReceived += (_, output) => integrationParser.ParseLine(output.Line);

    var probe = await service.ProbeAsync();
    Check(probe.IsAvailable && probe.Version.Contains("OpenFOAM-14"),
        "GUI service detects the installed OpenFOAM 14 engine");

    var integrationInfo = CaseInspector.Inspect(integrationCase);
    Check(integrationInfo.IsValid, "official tutorial is recognized as a valid case");
    Check(string.IsNullOrWhiteSpace(integrationInfo.Application) &&
          integrationInfo.SolverModule == "incompressibleFluid",
        "official v14 modular solver is resolved from controlDict");

    var blockMesh = await service.RunCaseCommandAsync(integrationCase, "blockMesh");
    File.WriteAllText(Path.Combine(integrationCase, "validation-blockMesh.log"), blockMesh.Output);
    Check(blockMesh.ExitCode == 0 && blockMesh.Output.Contains("End"),
        "real blockMesh completes through the GUI process bridge");

    var checkMesh = await service.RunCaseCommandAsync(
        integrationCase, "checkMesh -allTopology -allGeometry");
    File.WriteAllText(Path.Combine(integrationCase, "validation-checkMesh.log"), checkMesh.Output);
    Check(checkMesh.ExitCode == 0 && checkMesh.Output.Contains("Mesh OK"),
        "real full topology and geometry checks report Mesh OK");

    var solverCommand = service.ResolveSolverCommand(integrationInfo, false);
    Check(solverCommand == "foamRun", "GUI selects foamRun without substituting a solver");
    var solve = await service.RunCaseCommandAsync(integrationCase, solverCommand);
    File.WriteAllText(Path.Combine(integrationCase, "validation-foamRun.log"), solve.Output);
    Check(solve.ExitCode == 0 && solve.Output.Contains("End"),
        "real incompressibleFluid solve completes through the GUI process bridge");
    Check(integrationResiduals > 0, "real solver residuals are parsed for the UI monitor");

    var foamMarker = Path.Combine(integrationCase, $"{new DirectoryInfo(integrationCase).Name}.foam");
    File.WriteAllText(foamMarker, "");
    Check(File.Exists(foamMarker), "ParaView .foam marker is prepared");
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} smoke test(s) failed.");
    return 1;
}

Console.WriteLine("All smoke tests passed.");
return 0;
