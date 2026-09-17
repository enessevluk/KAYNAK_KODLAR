using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RavenMapPanel;

public sealed record RustMapMetadata(int WorldSize, int Seed, bool SeedFromFileName, uint FormatVersion);

public static partial class RustMapMetadataReader
{
    private const int MaxFirstChunkSize = 16 * 1024 * 1024;

    public static RustMapMetadata Read(string mapPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapPath);
        if (!File.Exists(mapPath)) throw new FileNotFoundException("Rust MAP dosyası bulunamadı.", mapPath);

        using var stream = new FileStream(mapPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        var version = reader.ReadUInt32();
        if (version == 10)
            _ = reader.ReadInt64();
        else if (version != 9)
            throw new InvalidDataException($"Desteklenmeyen Rust MAP biçimi (sürüm {version}).");

        var flags = ReadVarUInt(stream);
        var decodedLength = checked((int)ReadVarUInt(stream));
        var compressed = (flags & 1UL) != 0;
        var storedLength = compressed ? checked((int)ReadVarUInt(stream)) : decodedLength;
        if (decodedLength <= 0 || decodedLength > MaxFirstChunkSize || storedLength <= 0 || storedLength > MaxFirstChunkSize)
            throw new InvalidDataException("Rust MAP veri bloğu geçersiz.");

        var stored = new byte[storedLength];
        stream.ReadExactly(stored);
        var payload = compressed ? DecodeLz4Block(stored, decodedLength) : stored;
        var worldSize = ReadWorldSize(payload);
        if (worldSize is < 1000 or > 6000)
            throw new InvalidDataException($"MAP dosyasındaki dünya boyutu geçersiz: {worldSize}.");

        var fileSeed = DetectSeedFromPath(mapPath, worldSize);
        var seed = fileSeed ?? CreateStableAnalysisSeed(mapPath);
        return new RustMapMetadata(worldSize, seed, fileSeed.HasValue, version);
    }

    private static int ReadWorldSize(ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        while (offset < payload.Length)
        {
            var key = ReadProtoVarUInt(payload, ref offset);
            var field = key >> 3;
            var wireType = key & 7;
            if (field == 1 && wireType == 0)
                return checked((int)ReadProtoVarUInt(payload, ref offset));

            SkipProtoValue(payload, ref offset, wireType);
        }
        throw new InvalidDataException("MAP dosyasında dünya boyutu bulunamadı.");
    }

    private static void SkipProtoValue(ReadOnlySpan<byte> payload, ref int offset, ulong wireType)
    {
        switch (wireType)
        {
            case 0:
                _ = ReadProtoVarUInt(payload, ref offset);
                break;
            case 1:
                offset = checked(offset + 8);
                break;
            case 2:
                offset = checked(offset + (int)ReadProtoVarUInt(payload, ref offset));
                break;
            case 5:
                offset = checked(offset + 4);
                break;
            default:
                throw new InvalidDataException("MAP metadata alanı desteklenmiyor.");
        }
        if (offset > payload.Length) throw new EndOfStreamException("MAP metadata alanı eksik.");
    }

    private static ulong ReadProtoVarUInt(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (offset >= data.Length) throw new EndOfStreamException("MAP metadata alanı eksik.");
            var value = data[offset++];
            result |= (ulong)(value & 0x7F) << shift;
            if ((value & 0x80) == 0) return result;
        }
        throw new InvalidDataException("MAP metadata sayısı geçersiz.");
    }

    private static ulong ReadVarUInt(Stream stream)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            var raw = stream.ReadByte();
            if (raw < 0) throw new EndOfStreamException("Rust MAP veri bloğu eksik.");
            var value = (byte)raw;
            result |= (ulong)(value & 0x7F) << shift;
            if ((value & 0x80) == 0) return result;
        }
        throw new InvalidDataException("Rust MAP blok uzunluğu geçersiz.");
    }

    private static byte[] DecodeLz4Block(ReadOnlySpan<byte> source, int outputLength)
    {
        var output = new byte[outputLength];
        var sourceOffset = 0;
        var outputOffset = 0;

        while (sourceOffset < source.Length)
        {
            var token = source[sourceOffset++];
            var literalLength = ReadLz4Length(source, ref sourceOffset, token >> 4);
            if (literalLength > source.Length - sourceOffset || literalLength > output.Length - outputOffset)
                throw new InvalidDataException("Rust MAP LZ4 literal bloğu geçersiz.");
            source.Slice(sourceOffset, literalLength).CopyTo(output.AsSpan(outputOffset));
            sourceOffset += literalLength;
            outputOffset += literalLength;
            if (sourceOffset == source.Length) break;

            if (sourceOffset + 2 > source.Length) throw new InvalidDataException("Rust MAP LZ4 eşleşmesi eksik.");
            var matchOffset = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(sourceOffset, 2));
            sourceOffset += 2;
            if (matchOffset == 0 || matchOffset > outputOffset) throw new InvalidDataException("Rust MAP LZ4 eşleşme uzaklığı geçersiz.");

            var matchLength = checked(ReadLz4Length(source, ref sourceOffset, token & 0x0F) + 4);
            if (matchLength > output.Length - outputOffset) throw new InvalidDataException("Rust MAP LZ4 eşleşme bloğu geçersiz.");
            for (var i = 0; i < matchLength; i++)
                output[outputOffset + i] = output[outputOffset - matchOffset + i];
            outputOffset += matchLength;
        }

        if (outputOffset != outputLength) throw new InvalidDataException("Rust MAP LZ4 bloğu beklenen uzunlukta açılmadı.");
        return output;
    }

    private static int ReadLz4Length(ReadOnlySpan<byte> source, ref int offset, int initial)
    {
        var length = initial;
        if (initial != 15) return length;
        byte next;
        do
        {
            if (offset >= source.Length) throw new InvalidDataException("Rust MAP LZ4 uzunluğu eksik.");
            next = source[offset++];
            length = checked(length + next);
        } while (next == byte.MaxValue);
        return length;
    }

    private static int? DetectSeedFromPath(string mapPath, int worldSize)
    {
        var values = NumberRegex().Matches(mapPath)
            .Select(match => int.TryParse(match.Value, out var value) ? value : 0)
            .Where(value => value > 0)
            .ToList();
        var sizeIndex = values.FindIndex(value => value == worldSize);
        if (sizeIndex < 0) return null;
        var seed = values.Skip(sizeIndex + 1).FirstOrDefault(value => value > 0);
        return seed > 0 ? seed : null;
    }

    private static int CreateStableAnalysisSeed(string mapPath)
    {
        using var stream = new FileStream(mapPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = SHA256.HashData(stream);
        var seed = BinaryPrimitives.ReadInt32LittleEndian(hash) & int.MaxValue;
        return seed == 0 ? 1 : seed;
    }

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex NumberRegex();
}
