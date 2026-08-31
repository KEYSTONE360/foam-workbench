using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoamWorkbench.Services;

public static class PorousReportGenerator
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static IReadOnlyList<string> Generate(
        PorousCaseSettings settings,
        DarcyAnalysisResult analytical,
        PorousResultSummary? result,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var jsonPath = Path.Combine(outputDirectory, "porous_cfd_report.json");
        var csvPath = Path.Combine(outputDirectory, "porous_layer_results.csv");
        var htmlPath = Path.Combine(outputDirectory, "porous_cfd_report.html");
        var validation = PorousPhysics.Validate(settings);
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(new
        {
            GeneratedAt = DateTimeOffset.Now,
            Configuration = settings,
            Validation = validation.Issues,
            Analytical = analytical,
            Cfd = result,
            ResidenceTimeDefinition = "nominal: layer thickness / volume-weighted through-flow velocity; not particle tracking RTD"
        }, jsonOptions), new UTF8Encoding(false));

        var csv = new StringBuilder("layerId,design_group,zone,material,thickness_m,permeability_m2,pore_size_min_um,pore_size_max_um,particle_size_um,porosity,source,source_reference,resistance_fraction,average_pressure_Pa,inlet_pressure_Pa,outlet_pressure_Pa,pressure_drop_Pa,average_velocity_m_s,through_velocity_m_s,nominal_residence_s\n");
        foreach (var layer in settings.Layers)
        {
            var darcy = analytical.Layers.First(item => item.LayerId == layer.Id);
            var cfd = result?.Layers.FirstOrDefault(item => item.LayerId == layer.Id);
            csv.AppendLine(string.Join(',',
                layer.Id,
                Csv(layer.DesignGroup),
                Csv(layer.Name),
                Csv(layer.DisplayNameEn),
                F(darcy.ThicknessMetres),
                F(darcy.ThroughPermeability),
                layer.PoreSizeMin is null ? "" : F(layer.PoreSizeMin.Value),
                layer.PoreSizeMax is null ? "" : F(layer.PoreSizeMax.Value),
                layer.ParticleSize is null ? "" : F(layer.ParticleSize.Value),
                layer.Porosity is null ? "" : F(layer.Porosity.Value),
                layer.ParameterSource,
                Csv(layer.ParameterSourceReference),
                F(darcy.ResistanceFraction),
                cfd is null ? "" : F(cfd.AveragePressurePa),
                cfd is null ? "" : F(cfd.AverageInletPressurePa),
                cfd is null ? "" : F(cfd.AverageOutletPressurePa),
                cfd is null ? "" : F(cfd.PressureDropPa),
                cfd is null ? "" : F(cfd.AverageVelocity),
                cfd is null ? "" : F(cfd.AverageThroughVelocity),
                cfd is null ? "" : F(cfd.NominalResidenceTime)));
        }
        File.WriteAllText(csvPath, csv.ToString(), new UTF8Encoding(false));

        File.WriteAllText(htmlPath, BuildHtml(settings, analytical, result, validation), new UTF8Encoding(false));
        return [htmlPath, csvPath, jsonPath];
    }

    private static string BuildHtml(
        PorousCaseSettings settings,
        DarcyAnalysisResult analytical,
        PorousResultSummary? result,
        PorousValidationResult validation)
    {
        var rows = string.Join(Environment.NewLine, settings.Layers.Select(layer =>
        {
            var darcy = analytical.Layers.First(item => item.LayerId == layer.Id);
            var cfd = result?.Layers.FirstOrDefault(item => item.LayerId == layer.Id);
            return $"<tr><td>{layer.Id}</td><td>{H(layer.DisplayNameEn)}<br><small>{H(layer.DisplayNameKo)}</small></td>" +
                   $"<td>{H(layer.MaterialType)}</td><td>{F(darcy.ThicknessMetres)}</td>" +
                   $"<td>{F(darcy.ThroughPermeability)}</td>" +
                   $"<td>{(layer.PoreSizeMin is null || layer.PoreSizeMax is null ? "—" : $"{F(layer.PoreSizeMin.Value)}–{F(layer.PoreSizeMax.Value)}")}</td>" +
                   $"<td>{F(darcy.ResistanceFraction * 100)}%</td>" +
                   $"<td>{(cfd is null ? "—" : F(cfd.AverageInletPressurePa))}</td>" +
                   $"<td>{(cfd is null ? "—" : F(cfd.AverageOutletPressurePa))}</td>" +
                   $"<td>{(cfd is null ? "—" : F(cfd.PressureDropPa))}</td>" +
                   $"<td>{(cfd is null ? "—" : F(cfd.AverageThroughVelocity))}</td>" +
                   $"<td>{(cfd is null ? "—" : F(cfd.NominalResidenceTime))}</td></tr>";
        }));
        var issues = string.Join("", validation.Issues.Select(issue =>
            $"<li class='{(issue.IsError ? "error" : "warning")}'>{H(issue.Field)} — {H(issue.Message)}</li>"));
        var gravityMetrics = "";
        if (settings.FlowMode == PorousFlowMode.GravityDrainage && result is not null)
        {
            var area = PorousUnitConverter.MillimetresToMetres(settings.DomainWidthMm) *
                       PorousUnitConverter.MillimetresToMetres(settings.TargetCellSizeMm);
            var qGravity = Math.Abs(result.OutletFlowRate);
            gravityMetrics = $"<p>Qgravity={F(qGravity)} m³/s · Ugravity={F(qGravity / area)} m/s</p>";
        }
        return $$"""
<!doctype html><html lang="ko"><head><meta charset="utf-8"><title>Foam Workbench Porous CFD Report</title>
<style>body{font-family:Segoe UI,Arial,sans-serif;background:#0b1220;color:#e7eefb;margin:32px}h1,h2{color:#63e6d5}.card{background:#111c2e;border:1px solid #263750;border-radius:14px;padding:20px;margin:16px 0}table{width:100%;border-collapse:collapse}th,td{padding:9px;border-bottom:1px solid #2b3b52;text-align:left}th{color:#88a2c5}.error{color:#ff8b98}.warning{color:#ffc66d}small{color:#9fb0c7}</style></head>
<body><h1>FOAM WORKBENCH — Porous Media CFD Report</h1>
<div class="card"><h2>Case configuration</h2><p>Preset: {{H(settings.PresetName)}} ({{H(settings.PresetId)}})</p><p>Source: {{H(settings.PresetSourceReference)}}</p><p>Flow mode: {{settings.FlowMode}} · Simulation: {{settings.SimulationType}} · Width: {{F(settings.DomainWidthMm)}} mm</p>
<p>Water: ρ={{F(settings.Density)}} kg/m³, μ={{F(settings.DynamicViscosity)}} Pa·s · Gravity: ({{F(settings.GravityX)}} {{F(settings.GravityY)}} {{F(settings.GravityZ)}}) m/s²</p>
<p>OpenFOAM: Foundation 14, foamRun/incompressibleFluid, fvModels/porosityForce + buoyancyForce</p></div>
<div class="card"><h2>Analytical Darcy calculation</h2><p>Equivalent intrinsic permeability: {{F(analytical.EquivalentPermeability)}} m²</p><p>Hydraulic conductivity: {{F(analytical.HydraulicConductivity)}} m/s</p><p>Acceptance minimum: {{(settings.MinimumHydraulicConductivity is null ? "not specified" : $"{F(settings.MinimumHydraulicConductivity.Value)} m/s — {(analytical.HydraulicConductivity >= settings.MinimumHydraulicConductivity.Value ? "PASS" : "FAIL")}")}}</p><p>CFD/analytical tolerance: {{(settings.CfdAnalyticalTolerancePercent is null ? "not specified" : $"{F(settings.CfdAnalyticalTolerancePercent.Value)}%")}}</p><p>Individual-zone bottleneck: {{H(analytical.Bottleneck.DisplayName)}} — {{F(analytical.Bottleneck.ResistanceFraction * 100)}}%</p><p>Design-stage bottleneck: Group {{H(analytical.BottleneckGroup.GroupId)}} ({{H(analytical.BottleneckGroup.DisplayName)}}) — {{F(analytical.BottleneckGroup.ResistanceFraction * 100)}}%</p></div>
<div class="card"><h2>CFD vs analytical</h2><p>{{(result is null ? "CFD result not loaded" : $"CFD k_eff={F(result.CfdEquivalentPermeability)} m² · CFD K={F(result.CfdHydraulicConductivity)} m/s · Difference={F(result.CfdAnalyticalDifferencePercent)}%")}}</p><p><small>CFD k_eff = μ |Qout| L / [A(ΔP + ρ|gy|L)] for this saturated vertical 1-D stack.</small></p></div>
<div class="card"><h2>Layers and CFD results</h2><table><thead><tr><th>#</th><th>Layer</th><th>Type</th><th>L [m]</th><th>k [m²]</th><th>Pore [µm]</th><th>Resistance</th><th>Pin [Pa]</th><th>Pout [Pa]</th><th>ΔP [Pa]</th><th>Uy avg [m/s]</th><th>Nominal t [s]</th></tr></thead><tbody>{{rows}}</tbody></table>
<p><small>Nominal residence time = layer thickness / volume-weighted through-flow velocity. It is not a particle-tracking residence-time distribution.</small></p></div>
<div class="card"><h2>Flow balance</h2><p>{{(result?.FlowBalance is null ? "CFD result not loaded" : $"Inlet={F(result.FlowBalance.InletFlowRate)}, Outlet={F(result.FlowBalance.OutletFlowRate)}, Difference={F(result.FlowBalance.DifferencePercent)}%, {(result.FlowBalance.Pass ? "PASS" : "WARNING")}")}}</p>{{gravityMetrics}}</div>
<div class="card"><h2>Validation</h2><ul>{{issues}}</ul></div></body></html>
""";
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string H(string value) => WebUtility.HtmlEncode(value);
    private static string F(double value) => double.IsFinite(value) ? value.ToString("G8", Inv) : "—";
}
