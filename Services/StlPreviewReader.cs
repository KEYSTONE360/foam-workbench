using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace FoamWorkbench.Services;

public static class StlPreviewReader
{
    public static CadPreviewData Read(string path, int maxDisplayTriangles = 250_000)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 84)
            throw new InvalidDataException("미리보기 STL 파일이 너무 짧습니다.");

        Span<byte> header = stackalloc byte[84];
        stream.ReadExactly(header);
        var binaryCount = BinaryPrimitives.ReadUInt32LittleEndian(header[80..84]);
        var isBinary = 84L + binaryCount * 50L == stream.Length;
        stream.Position = 0;

        return isBinary
            ? ReadBinary(stream, checked((int)binaryCount), maxDisplayTriangles)
            : ReadAscii(stream, maxDisplayTriangles);
    }

    private static CadPreviewData ReadBinary(Stream stream, int count, int maxDisplayTriangles)
    {
        stream.Position = 84;
        Span<byte> triangle = stackalloc byte[50];
        var stride = Math.Max(1, (int)Math.Ceiling(count / (double)maxDisplayTriangles));
        var triangles = new List<PreviewTriangle>(Math.Min(count, maxDisplayTriangles));
        var bounds = new PreviewBoundsAccumulator();

        for (var i = 0; i < count; i++)
        {
            stream.ReadExactly(triangle);
            var a = ReadBinaryPoint(triangle, 12);
            var b = ReadBinaryPoint(triangle, 24);
            var c = ReadBinaryPoint(triangle, 36);
            bounds.Add(a); bounds.Add(b); bounds.Add(c);
            if (i % stride == 0) triangles.Add(new PreviewTriangle(a, b, c));
        }

        return Build(triangles, bounds.Result(), count, stride > 1);
    }

    private static Point3 ReadBinaryPoint(ReadOnlySpan<byte> data, int offset) => new(
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data[offset..])),
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 4)..])),
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 8)..])));

    private static CadPreviewData ReadAscii(Stream stream, int maxDisplayTriangles)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, true, 64 * 1024, leaveOpen: true);
        var all = new List<PreviewTriangle>();
        var vertices = new List<Point3>(3);
        var bounds = new PreviewBoundsAccumulator();

        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("vertex ", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            var point = new Point3(
                double.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture),
                double.Parse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture),
                double.Parse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture));
            bounds.Add(point);
            vertices.Add(point);
            if (vertices.Count != 3) continue;
            all.Add(new PreviewTriangle(vertices[0], vertices[1], vertices[2]));
            vertices.Clear();
        }

        if (all.Count == 0) throw new InvalidDataException("STL에서 미리보기 삼각형을 찾지 못했습니다.");
        var stride = Math.Max(1, (int)Math.Ceiling(all.Count / (double)maxDisplayTriangles));
        var display = stride == 1 ? all : all.Where((_, index) => index % stride == 0).ToList();
        return Build(display, bounds.Result(), all.Count, stride > 1);
    }

    private static CadPreviewData Build(
        IReadOnlyList<PreviewTriangle> triangles,
        GeometryBounds bounds,
        int originalCount,
        bool reduced) => new()
        {
            Triangles = triangles,
            Bounds = bounds,
            OriginalTriangleCount = originalCount,
            WasDisplayReduced = reduced,
            ConversionOutput = ""
        };

    private sealed class PreviewBoundsAccumulator
    {
        private double _minX = double.PositiveInfinity;
        private double _minY = double.PositiveInfinity;
        private double _minZ = double.PositiveInfinity;
        private double _maxX = double.NegativeInfinity;
        private double _maxY = double.NegativeInfinity;
        private double _maxZ = double.NegativeInfinity;

        public void Add(Point3 p)
        {
            _minX = Math.Min(_minX, p.X); _maxX = Math.Max(_maxX, p.X);
            _minY = Math.Min(_minY, p.Y); _maxY = Math.Max(_maxY, p.Y);
            _minZ = Math.Min(_minZ, p.Z); _maxZ = Math.Max(_maxZ, p.Z);
        }

        public GeometryBounds Result()
        {
            if (!double.IsFinite(_minX)) throw new InvalidDataException("STL 경계를 계산할 수 없습니다.");
            return new GeometryBounds(
                new Point3(_minX, _minY, _minZ),
                new Point3(_maxX, _maxY, _maxZ));
        }
    }
}
