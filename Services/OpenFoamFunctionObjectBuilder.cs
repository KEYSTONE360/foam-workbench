using System.Globalization;
using System.Text;

namespace FoamWorkbench.Services;

public static class OpenFoamFunctionObjectBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Build(CadProjectSettings settings)
    {
        Validate(settings);

        var text = new StringBuilder();
        text.AppendLine("// OpenFOAM functionObjects generated from the '결과 변수' UI.");
        text.AppendLine("// This file is included verbatim inside controlDict/functions.");

        if (settings.CalculateResiduals)
        {
            var fields = settings.Turbulence == TurbulenceChoice.KOmegaSst ? "U p k omega" : "U p";
            text.AppendLine($$"""

residuals
{
    type            residuals;
    libs            ("libutilityFunctionObjects.so");
    fields          ({{fields}});
    writeControl    timeStep;
    writeInterval   1;
}
""");
        }

        if (settings.CalculateForces)
        {
            text.AppendLine($$"""

forces
{
    type            forces;
    libs            ("libforces.so");
    patches         {{PatchList(settings.ForcePatches)}};
    rho             rhoInf;
    rhoInf          {{F(settings.Density)}};
    CofR            {{Vector(settings.CentreOfRotationText, "회전 중심", allowZero: true)}};
    pitchAxis       {{Vector(settings.PitchAxisText, "피치 축")}};
    writeControl    timeStep;
    writeInterval   1;
}
""");
        }

        if (settings.CalculateForceCoefficients)
        {
            text.AppendLine($$"""

forceCoeffs
{
    type            forceCoeffs;
    libs            ("libforces.so");
    patches         {{PatchList(settings.ForcePatches)}};
    rho             rhoInf;
    rhoInf          {{F(settings.Density)}};
    magUInf         {{F(settings.Velocity)}};
    lRef            {{F(settings.ReferenceLength)}};
    Aref            {{F(settings.ReferenceArea)}};
    CofR            {{Vector(settings.CentreOfRotationText, "회전 중심", allowZero: true)}};
    liftDir         {{Vector(settings.LiftDirectionText, "양력 방향")}};
    dragDir         {{Vector(settings.DragDirectionText, "항력 방향")}};
    pitchAxis       {{Vector(settings.PitchAxisText, "피치 축")}};
    writeControl    timeStep;
    writeInterval   1;
}
""");
        }

        AppendFieldObject(text, "wallShearStress", "wallShearStress", settings.CalculateWallShearStress);
        AppendFieldObject(text, "yPlus", "yPlus", settings.CalculateYPlus);
        AppendFieldObject(text, "Q", "Q", settings.CalculateQCriterion, "    U               U;\n    field           $U;");
        AppendFieldObject(text, "vorticity", "vorticity", settings.CalculateVorticity,
            "    U               U;\n    field           $U;");
        AppendFieldObject(text, "turbulenceIntensity", "turbulenceIntensity",
            settings.CalculateTurbulenceIntensity);

        if (settings.CalculateFieldAverage)
        {
            var fields = WordList(settings.AveragedFields, "시간평균 필드");
            text.AppendLine($$"""

fieldAverage
{
    type            fieldAverage;
    libs            ("libfieldFunctionObjects.so");
    mean            yes;
    prime2Mean      yes;
    fields          {{fields}};
    executeControl  writeTime;
    writeControl    writeTime;
}
""");
        }

        if (!string.IsNullOrWhiteSpace(settings.CustomFunctionObjects))
        {
            text.AppendLine();
            text.AppendLine("// User functionObjects — preserved exactly as entered in the UI.");
            text.AppendLine(settings.CustomFunctionObjects.Trim());
            text.AppendLine();
        }

        return text.ToString().Replace("\r\n", "\n");
    }

    private static void AppendFieldObject(
        StringBuilder text,
        string name,
        string type,
        bool enabled,
        string? extra = null)
    {
        if (!enabled) return;
        text.AppendLine();
        text.AppendLine(name);
        text.AppendLine("{");
        text.AppendLine($"    type            {type};");
        text.AppendLine("    libs            (\"libfieldFunctionObjects.so\");");
        if (!string.IsNullOrWhiteSpace(extra)) text.AppendLine(extra);
        text.AppendLine("    executeControl  writeTime;");
        text.AppendLine("    writeControl    writeTime;");
        text.AppendLine("}");
    }

    private static void Validate(CadProjectSettings settings)
    {
        if ((settings.CalculateForces || settings.CalculateForceCoefficients) && settings.Density <= 0)
            throw new ArgumentException("힘 계산의 유체 밀도는 0보다 커야 합니다.");
        if (settings.CalculateForceCoefficients &&
            (settings.ReferenceArea <= 0 || settings.ReferenceLength <= 0))
            throw new ArgumentException("계수 계산의 기준 면적과 기준 길이는 0보다 커야 합니다.");

        if (settings.CalculateForces || settings.CalculateForceCoefficients)
        {
            _ = PatchList(settings.ForcePatches);
            _ = Vector(settings.CentreOfRotationText, "회전 중심", allowZero: true);
            _ = Vector(settings.PitchAxisText, "피치 축");
        }

        if (settings.CalculateForceCoefficients)
        {
            _ = Vector(settings.DragDirectionText, "항력 방향");
            _ = Vector(settings.LiftDirectionText, "양력 방향");
        }

        if (settings.CalculateFieldAverage)
            _ = WordList(settings.AveragedFields, "시간평균 필드");
    }

    private static string Vector(string value, string label, bool allowZero = false)
    {
        var normalized = value.Trim().Trim('(', ')').Replace(',', ' ');
        var parts = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 ||
            !double.TryParse(parts[0], NumberStyles.Float, Inv, out var x) ||
            !double.TryParse(parts[1], NumberStyles.Float, Inv, out var y) ||
            !double.TryParse(parts[2], NumberStyles.Float, Inv, out var z))
            throw new ArgumentException($"{label}은 `x y z` 형식의 세 숫자여야 합니다.");

        if (!allowZero && x * x + y * y + z * z <= 1e-24)
            throw new ArgumentException($"{label} 벡터는 0일 수 없습니다.");

        return $"({F(x)} {F(y)} {F(z)})";
    }

    private static string PatchList(string value)
    {
        var values = Tokens(value);
        if (values.Length == 0)
            throw new ArgumentException("힘을 적분할 벽 패치를 하나 이상 입력하세요.");
        return $"({string.Join(' ', values.Select(QuoteWord))})";
    }

    private static string WordList(string value, string label)
    {
        var values = Tokens(value);
        if (values.Length == 0) throw new ArgumentException($"{label}를 하나 이상 입력하세요.");
        return $"({string.Join(' ', values.Select(QuoteWord))})";
    }

    private static string[] Tokens(string value) => value.Trim().Trim('(', ')')
        .Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(token => token.Trim('"'))
        .Where(token => token.Length > 0)
        .ToArray();

    private static string QuoteWord(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private static string F(double value) => value.ToString("G17", Inv);
}
