using System.Globalization;
using System.IO;
using System.Text;

namespace FoamWorkbench.Services;

public sealed class CadPreviewService(OpenFoamService openFoam)
{
    public async Task<CadPreviewData> BuildAsync(
        string cadFilePath,
        CadLengthUnit unit,
        double cadSurfaceSize,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(cadFilePath))
            throw new FileNotFoundException("미리보기할 CAD 파일을 찾을 수 없습니다.", cadFilePath);
        if (cadSurfaceSize <= 0)
            throw new ArgumentException("CAD 표면 요소 크기는 0보다 커야 합니다.");

        var extension = Path.GetExtension(cadFilePath).ToLowerInvariant();
        var supported = new[] { ".step", ".stp", ".iges", ".igs", ".brep", ".stl" };
        if (!supported.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException("STEP, STP, IGES, IGS, BREP, STL 미리보기를 지원합니다.");

        var previewRoot = Path.Combine(
            Path.GetTempPath(), "FoamWorkbench", "CadPreview", Guid.NewGuid().ToString("N"));
        var geometryDirectory = Path.Combine(previewRoot, "constant", "geometry");
        Directory.CreateDirectory(geometryDirectory);
        var output = new StringBuilder();

        try
        {
            var sourceName = $"source{extension}";
            File.Copy(cadFilePath, Path.Combine(geometryDirectory, sourceName), overwrite: true);
            if (extension == ".stl")
            {
                File.Copy(
                    Path.Combine(geometryDirectory, sourceName),
                    Path.Combine(geometryDirectory, "previewRaw.stl"),
                    overwrite: true);
            }
            else
            {
                var size = cadSurfaceSize.ToString("G17", CultureInfo.InvariantCulture);
                var gmsh = await openFoam.RunCaseCommandAsync(
                    previewRoot,
                    $"gmsh 'constant/geometry/{sourceName}' -2 -format stl " +
                    $"-o 'constant/geometry/previewRaw.stl' -clmin {size} -clmax {size} " +
                    "-setnumber Mesh.Binary 0",
                    cancellationToken);
                output.AppendLine(gmsh.Output);
                if (gmsh.ExitCode != 0)
                    throw new InvalidOperationException("Gmsh/OpenCASCADE가 CAD 미리보기 표면을 만들지 못했습니다.");
            }

            var scale = unit == CadLengthUnit.Millimetre ? 0.001 : 1.0;
            var factor = scale.ToString("G17", CultureInfo.InvariantCulture);
            var transform = await openFoam.RunCaseCommandAsync(
                previewRoot,
                $"surfaceTransformPoints \"scale=({factor} {factor} {factor})\" " +
                "'constant/geometry/previewRaw.stl' 'constant/geometry/preview.stl'",
                cancellationToken);
            output.AppendLine(transform.Output);
            if (transform.ExitCode != 0)
                throw new InvalidOperationException("OpenFOAM CAD 단위 변환에 실패했습니다.");

            var parsed = StlPreviewReader.Read(Path.Combine(geometryDirectory, "preview.stl"));
            return new CadPreviewData
            {
                Triangles = parsed.Triangles,
                Bounds = parsed.Bounds,
                OriginalTriangleCount = parsed.OriginalTriangleCount,
                WasDisplayReduced = parsed.WasDisplayReduced,
                ConversionOutput = output.ToString()
            };
        }
        finally
        {
            try
            {
                if (Directory.Exists(previewRoot)) Directory.Delete(previewRoot, recursive: true);
            }
            catch
            {
                // A stale preview cache is harmless and can be removed by the OS later.
            }
        }
    }
}
