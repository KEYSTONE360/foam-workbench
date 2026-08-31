namespace FoamWorkbench.Services;

public static class PorousPhysics
{
    private const double Tiny = 1e-30;

    public static double PermeabilityToDarcyResistance(double permeability)
    {
        RequirePositiveFinite(permeability, nameof(permeability));
        return 1.0 / permeability;
    }

    public static double DarcyResistanceToPermeability(double resistance)
    {
        RequirePositiveFinite(resistance, nameof(resistance));
        return 1.0 / resistance;
    }

    public static double KinematicViscosity(double dynamicViscosity, double density)
    {
        RequirePositiveFinite(dynamicViscosity, nameof(dynamicViscosity));
        RequirePositiveFinite(density, nameof(density));
        return dynamicViscosity / density;
    }

    public static double WaterHeadPressure(double density, double gravityMagnitude, double headMetres)
    {
        RequirePositiveFinite(density, nameof(density));
        RequirePositiveFinite(gravityMagnitude, nameof(gravityMagnitude));
        if (!double.IsFinite(headMetres) || headMetres < 0)
            throw new ArgumentOutOfRangeException(nameof(headMetres));
        return density * gravityMagnitude * headMetres;
    }

    public static double KozenyCarman(double particleDiameterMetres, double porosity)
    {
        RequirePositiveFinite(particleDiameterMetres, nameof(particleDiameterMetres));
        if (!double.IsFinite(porosity) || porosity <= 0 || porosity >= 1)
            throw new ArgumentOutOfRangeException(nameof(porosity), "Porosity must be between 0 and 1.");
        return particleDiameterMetres * particleDiameterMetres * Math.Pow(porosity, 3) /
               (180.0 * Math.Pow(1 - porosity, 2));
    }

    public static double NominalResidenceTime(double thicknessMetres, double averageThroughVelocity)
    {
        RequirePositiveFinite(thicknessMetres, nameof(thicknessMetres));
        if (!double.IsFinite(averageThroughVelocity) || Math.Abs(averageThroughVelocity) <= Tiny)
            return double.PositiveInfinity;
        return thicknessMetres / Math.Abs(averageThroughVelocity);
    }

    public static PorousFlowBalance CalculateFlowBalance(double inlet, double outlet, double passTolerancePercent = 1)
    {
        if (!double.IsFinite(inlet) || !double.IsFinite(outlet))
            throw new ArgumentException("Flow rates must be finite.");
        var denominator = Math.Max(Math.Abs(inlet), Tiny);
        var difference = Math.Abs(Math.Abs(inlet) - Math.Abs(outlet)) / denominator * 100.0;
        return new PorousFlowBalance(inlet, outlet, difference, difference <= passTolerancePercent);
    }

    public static PorousValidationResult Validate(PorousCaseSettings settings)
    {
        var issues = new List<PorousValidationIssue>();
        ErrorUnlessPositiveFinite(issues, "Domain Width", settings.DomainWidthMm, "Domain width must be greater than 0 mm.");
        ErrorUnlessPositiveFinite(issues, "Density", settings.Density, "Density must be greater than 0 kg/m³.");
        ErrorUnlessPositiveFinite(issues, "Dynamic Viscosity", settings.DynamicViscosity, "Dynamic viscosity must be greater than 0 Pa·s.");
        ErrorUnlessPositiveFinite(issues, "Target cell size Y", settings.TargetCellSizeMm, "Target cell size must be greater than 0 mm.");
        if (settings.MinimumCellsPerLayer < 1)
            issues.Add(new("Minimum cells through each layer", "Minimum cells must be at least 1.", true));
        if (settings.Layers.Count == 0)
            issues.Add(new("Layers", "At least one porous layer is required.", true));
        if (settings.Layers.Select(layer => layer.Name).Distinct(StringComparer.Ordinal).Count() != settings.Layers.Count)
            issues.Add(new("Cell zones", "Every layer must have a unique OpenFOAM cellZone name.", true));

        foreach (var layer in settings.Layers)
        {
            var label = $"Layer {layer.Id} — {layer.DisplayNameEn}";
            if (layer.Thickness is null)
                issues.Add(new($"{label} — thickness", "INPUT REQUIRED: thickness [mm] is missing.", true));
            else if (!IsPositiveFinite(layer.Thickness.Value))
                issues.Add(new($"{label} — thickness", "Thickness must be finite and greater than 0 mm.", true));

            if (layer.PermeabilityType == PorousPermeabilityType.Isotropic)
            {
                if (layer.Permeability is null)
                    issues.Add(new($"{label} — permeability", "INPUT REQUIRED: intrinsic permeability k [m²] is missing.", true));
                else if (!IsPositiveFinite(layer.Permeability.Value))
                    issues.Add(new($"{label} — permeability", "Intrinsic permeability k must be finite and greater than 0 m².", true));
                else
                    AddPermeabilityWarning(issues, label, layer.Permeability.Value);
            }
            else
            {
                ValidateTensor(issues, label, "Kx", layer.PermeabilityX);
                ValidateTensor(issues, label, "Ky", layer.PermeabilityY);
                ValidateTensor(issues, label, "Kz", layer.PermeabilityZ);
            }

            if (layer.Porosity is not null &&
                (!double.IsFinite(layer.Porosity.Value) || layer.Porosity.Value <= 0 || layer.Porosity.Value >= 1))
                issues.Add(new($"{label} — porosity", "Porosity must be finite and strictly between 0 and 1.", true));
            if (layer.PoreSizeMin is not null && !IsPositiveFinite(layer.PoreSizeMin.Value))
                issues.Add(new($"{label} — minimum pore size", "Minimum pore size must be finite and greater than 0 µm.", true));
            if (layer.PoreSizeMax is not null && !IsPositiveFinite(layer.PoreSizeMax.Value))
                issues.Add(new($"{label} — maximum pore size", "Maximum pore size must be finite and greater than 0 µm.", true));
            if (layer.PoreSizeMin is not null && layer.PoreSizeMax is not null &&
                layer.PoreSizeMin.Value > layer.PoreSizeMax.Value)
                issues.Add(new($"{label} — pore size range", "Minimum pore size cannot exceed maximum pore size.", true));
            if (layer.ParticleSize is not null && !IsPositiveFinite(layer.ParticleSize.Value))
                issues.Add(new($"{label} — particle size", "Particle size must be finite and greater than 0 µm.", true));
            if (!double.IsFinite(layer.ForchheimerCoefficient) || layer.ForchheimerCoefficient < 0)
                issues.Add(new($"{label} — Forchheimer", "Forchheimer coefficient must be finite and at least 0 m⁻¹.", true));
            if (layer.Category == PorousMaterialCategory.FiberMembrane &&
                layer.PermeabilityType == PorousPermeabilityType.Isotropic)
                issues.Add(new($"{label} — isotropy",
                    "Physical warning: a fibre layer may be anisotropic; use measured Kx/Ky/Kz when available.", false));

            if (layer.Thickness is > 0 && settings.TargetCellSizeMm > 0 &&
                layer.Thickness.Value / settings.TargetCellSizeMm < settings.MinimumCellsPerLayer)
                issues.Add(new($"{label} — mesh",
                    $"Mesh will increase this layer to {settings.MinimumCellsPerLayer} cells because thickness/dy is too small.", false));
        }

        var estimatedLayers = settings.Layers.Count(layer => layer.ParameterSource == PorousParameterSource.Estimated);
        if (estimatedLayers > 0)
            issues.Add(new("Permeability source",
                $"Physical warning: {estimatedLayers} layer permeability value(s) are marked Estimated; replace them with literature or experimental bulk intrinsic permeability when available.", false));

        if (settings.MinimumHydraulicConductivity is not null &&
            !IsPositiveFinite(settings.MinimumHydraulicConductivity.Value))
            issues.Add(new("Hydraulic conductivity acceptance criterion",
                "Minimum hydraulic conductivity must be finite and greater than 0 m/s.", true));
        if (settings.CfdAnalyticalTolerancePercent is not null &&
            !IsPositiveFinite(settings.CfdAnalyticalTolerancePercent.Value))
            issues.Add(new("CFD/analytical tolerance",
                "CFD/analytical tolerance must be finite and greater than 0 percent.", true));

        if (settings.FlowMode == PorousFlowMode.RainfallFlux)
            ErrorUnlessPositiveFinite(issues, "Rainfall", settings.RainfallMmPerHour, "Rainfall must be greater than 0 mm/hr.");
        if (settings.FlowMode == PorousFlowMode.WaterHead &&
            (!double.IsFinite(settings.WaterHeadMm) || settings.WaterHeadMm < 0))
            issues.Add(new("Water Head", "Water head must be finite and at least 0 mm.", true));
        if (settings.SimulationType == PorousSimulationType.Transient)
            ErrorUnlessPositiveFinite(issues, "Delta t", settings.DeltaT, "Transient deltaT must be greater than 0 s.");
        if (settings.EndTime < 1 || settings.WriteInterval < 1)
            issues.Add(new("Solver control", "End time and write interval must be positive integers.", true));

        var gravityMagnitude = Math.Sqrt(
            settings.GravityX * settings.GravityX +
            settings.GravityY * settings.GravityY +
            settings.GravityZ * settings.GravityZ);
        if (settings.GravityEnabled && (!double.IsFinite(gravityMagnitude) || gravityMagnitude <= 0))
            issues.Add(new("Gravity", "Enabled gravity must have a finite non-zero vector.", true));
        if (settings.FlowMode == PorousFlowMode.GravityDrainage && !settings.GravityEnabled)
            issues.Add(new("Gravity Drainage", "Gravity Drainage requires gravity to be enabled.", true));

        var rainfallVelocity = PorousUnitConverter.MillimetresPerHourToMetresPerSecond(
            Math.Max(0, settings.RainfallMmPerHour));
        if (rainfallVelocity > 0.01 && settings.Layers.All(layer => layer.ForchheimerCoefficient == 0))
            issues.Add(new("Darcy range",
                "Physical warning: high superficial velocity may require measured Forchheimer coefficients.", false));

        return new PorousValidationResult { Issues = issues };
    }

    public static DarcyAnalysisResult CalculateAnalytical(PorousCaseSettings settings)
    {
        var validation = Validate(settings);
        if (!validation.IsValid)
            throw new InvalidOperationException("Analytical Darcy calculation requires valid thickness and permeability for every layer.");

        var raw = settings.Layers.Select(layer =>
        {
            var thickness = PorousUnitConverter.MillimetresToMetres(layer.Thickness!.Value);
            var permeability = layer.ThroughPermeability!.Value;
            return (Layer: layer, Thickness: thickness, Permeability: permeability,
                Resistance: thickness / permeability);
        }).ToArray();
        var totalThickness = raw.Sum(item => item.Thickness);
        var totalResistance = raw.Sum(item => item.Resistance);
        var equivalent = totalThickness / totalResistance;
        var gravityMagnitude = Math.Sqrt(
            settings.GravityX * settings.GravityX +
            settings.GravityY * settings.GravityY +
            settings.GravityZ * settings.GravityZ);
        if (gravityMagnitude <= 0) gravityMagnitude = 9.81;
        var hydraulic = equivalent * settings.Density * gravityMagnitude / settings.DynamicViscosity;
        var rainfall = PorousUnitConverter.MillimetresPerHourToMetresPerSecond(settings.RainfallMmPerHour);
        var layerResults = raw.Select(item => new LayerDarcyResult(
            item.Layer.Id,
            item.Layer.Name,
            item.Layer.DisplayNameEn,
            item.Thickness,
            item.Permeability,
            item.Resistance,
            item.Resistance / totalResistance)).ToArray();
        var bottleneck = layerResults.MaxBy(item => item.Resistance)!;
        var groups = raw
            .GroupBy(item => string.IsNullOrWhiteSpace(item.Layer.DesignGroup)
                ? item.Layer.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : item.Layer.DesignGroup,
                StringComparer.Ordinal)
            .Select(group =>
            {
                var resistance = group.Sum(item => item.Resistance);
                return new LayerResistanceGroupResult(
                    group.Key,
                    string.Join(" + ", group.Select(item => item.Layer.DisplayNameEn)),
                    resistance,
                    resistance / totalResistance,
                    group.Select(item => item.Layer.Name).ToArray());
            })
            .ToArray();
        var bottleneckGroup = groups.MaxBy(item => item.Resistance)!;

        return new DarcyAnalysisResult
        {
            TotalThicknessMetres = totalThickness,
            EquivalentPermeability = equivalent,
            HydraulicConductivity = hydraulic,
            RequiredRainfallVelocity = rainfall,
            SafetyFactor = rainfall > Tiny ? hydraulic / rainfall : double.PositiveInfinity,
            Layers = layerResults,
            Bottleneck = bottleneck,
            Groups = groups,
            BottleneckGroup = bottleneckGroup
        };
    }

    private static void ValidateTensor(
        ICollection<PorousValidationIssue> issues, string label, string component, double? value)
    {
        if (value is null)
            issues.Add(new($"{label} — {component}", $"INPUT REQUIRED: anisotropic {component} [m²] is missing.", true));
        else if (!IsPositiveFinite(value.Value))
            issues.Add(new($"{label} — {component}", $"{component} must be finite and greater than 0 m².", true));
        else
            AddPermeabilityWarning(issues, label, value.Value);
    }

    private static void AddPermeabilityWarning(
        ICollection<PorousValidationIssue> issues, string label, double permeability)
    {
        if (permeability < 1e-18 || permeability > 1e-6)
            issues.Add(new($"{label} — permeability range",
                "Physical warning: this bulk intrinsic permeability is outside a broad common engineering range; verify the source and units.", false));
    }

    private static void ErrorUnlessPositiveFinite(
        ICollection<PorousValidationIssue> issues, string field, double value, string message)
    {
        if (!IsPositiveFinite(value)) issues.Add(new(field, message, true));
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;

    private static void RequirePositiveFinite(double value, string name)
    {
        if (!IsPositiveFinite(value)) throw new ArgumentOutOfRangeException(name);
    }
}
