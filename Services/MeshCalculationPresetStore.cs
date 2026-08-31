using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace FoamWorkbench.Services;

public static class MeshCalculationPresetStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(string filePath, MeshCalculationPreset preset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(preset);
        Validate(preset);

        var fullPath = Path.GetFullPath(filePath);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(preset, JsonOptions), new UTF8Encoding(false));
    }

    public static MeshCalculationPreset Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var preset = JsonSerializer.Deserialize<MeshCalculationPreset>(
            File.ReadAllText(Path.GetFullPath(filePath), Encoding.UTF8), JsonOptions)
            ?? throw new InvalidDataException("설정 프리셋 파일이 비어 있습니다.");
        Validate(preset);
        return preset;
    }

    private static void Validate(MeshCalculationPreset preset)
    {
        if (preset.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException(
                $"지원하지 않는 프리셋 버전입니다: {preset.SchemaVersion} (지원: {CurrentSchemaVersion})");
        if (!Enum.IsDefined(preset.CadUnit) || !Enum.IsDefined(preset.AnalysisType) ||
            !Enum.IsDefined(preset.FlowAxis) || !Enum.IsDefined(preset.Turbulence))
            throw new InvalidDataException("프리셋에 알 수 없는 해석 옵션이 포함되어 있습니다.");
        if (preset.ProcessCount < 1)
            throw new InvalidDataException("병렬 프로세스 수는 1 이상이어야 합니다.");
        if (preset.WriteInterval < 1)
            throw new InvalidDataException("결과 저장 간격은 1 이상이어야 합니다.");
    }
}
