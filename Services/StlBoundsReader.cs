using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace FoamWorkbench.Services;

public static class StlBoundsReader
{
    public static GeometryBounds Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length < 84)
            throw new InvalidDataException("STL 파일이 너무 짧습니다.");

        Span<byte> header = stackalloc byte[84];
        stream.ReadExactly(header);
        var triangleCount = BinaryPrimitives.ReadUInt32LittleEndian(header[80..84]);
        var expectedBinaryLength = 84L + triangleCount * 50L;
        stream.Position = 0;

        return expectedBinaryLength == stream.Length
            ? ReadBinary(stream, triangleCount)
            : ReadAscii(stream);
    }

    private static GeometryBounds ReadBinary(Stream stream, uint triangleCount)
    {
        stream.Position = 84;
        Span<byte> triangle = stackalloc byte[50];
        var accumulator = new BoundsAccumulator();
        for (uint i = 0; i < triangleCount; i++)
        {
            stream.ReadExactly(triangle);
            for (var vertex = 0; vertex < 3; vertex++)
            {
                var offset = 12 + vertex * 12;
                accumulator.Add(
                    BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(triangle[offset..])),
                    BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(triangle[(offset + 4)..])),
                    BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(triangle[(offset + 8)..])));
            }
        }
        return accumulator.Result();
    }

    private static GeometryBounds ReadAscii(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, true, 64 * 1024, leaveOpen: true);
        var accumulator = new BoundsAccumulator();
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("vertex ", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            accumulator.Add(
                double.Parse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture),
                double.Parse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture),
                double.Parse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture));
        }
        return accumulator.Result();
    }

    private sealed class BoundsAccumulator
    {
        private double _minX = double.PositiveInfinity;
        private double _minY = double.PositiveInfinity;
        private double _minZ = double.PositiveInfinity;
        private double _maxX = double.NegativeInfinity;
        private double _maxY = double.NegativeInfinity;
        private double _maxZ = double.NegativeInfinity;
        private long _count;

        public void Add(double x, double y, double z)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return;
            _minX = Math.Min(_minX, x);
            _minY = Math.Min(_minY, y);
            _minZ = Math.Min(_minZ, z);
            _maxX = Math.Max(_maxX, x);
            _maxY = Math.Max(_maxY, y);
            _maxZ = Math.Max(_maxZ, z);
            _count++;
        }

        public GeometryBounds Result()
        {
            if (_count == 0)
                throw new InvalidDataException("STL에서 유효한 꼭짓점을 찾지 못했습니다.");
            return new GeometryBounds(new Point3(_minX, _minY, _minZ), new Point3(_maxX, _maxY, _maxZ));
        }
    }
}
