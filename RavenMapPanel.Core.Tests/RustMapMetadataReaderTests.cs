using System.Text;
using Xunit;

namespace RavenMapPanel.Core.Tests;

public sealed class RustMapMetadataReaderTests
{
    [Fact]
    public void ReadsWorldSizeAndSeedWithoutUserInput()
    {
        var folder = Path.Combine(Path.GetTempPath(), "raven-map-metadata-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "proceduralmap.3600.1476575535.287.map");
        try
        {
            WriteUncompressedMap(path, 3600);
            var metadata = RustMapMetadataReader.Read(path);
            Assert.Equal(3600, metadata.WorldSize);
            Assert.Equal(1476575535, metadata.Seed);
            Assert.True(metadata.SeedFromFileName);
            Assert.Equal(10u, metadata.FormatVersion);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void CreatesStableSeedWhenMapDoesNotContainOne()
    {
        var folder = Path.Combine(Path.GetTempPath(), "raven-map-metadata-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "downloaded-map.map");
        try
        {
            WriteUncompressedMap(path, 4500);
            var first = RustMapMetadataReader.Read(path);
            var second = RustMapMetadataReader.Read(path);
            Assert.Equal(4500, first.WorldSize);
            Assert.Equal(first.Seed, second.Seed);
            Assert.InRange(first.Seed, 1, int.MaxValue);
            Assert.False(first.SeedFromFileName);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void WriteUncompressedMap(string path, int worldSize)
    {
        var payload = new List<byte> { 8 };
        WriteVarUInt(payload, (ulong)worldSize);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(10u);
        writer.Write(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        stream.WriteByte(0);
        WriteVarUInt(stream, (ulong)payload.Count);
        stream.Write(payload.ToArray());
    }

    private static void WriteVarUInt(List<byte> output, ulong value)
    {
        do
        {
            var next = (byte)(value & 0x7F);
            value >>= 7;
            output.Add((byte)(next | (value == 0 ? 0 : 0x80)));
        } while (value != 0);
    }

    private static void WriteVarUInt(Stream output, ulong value)
    {
        var bytes = new List<byte>();
        WriteVarUInt(bytes, value);
        output.Write(bytes.ToArray());
    }
}
