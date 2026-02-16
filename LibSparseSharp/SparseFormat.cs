using System.Buffers.Binary;

namespace LibSparseSharp;

public static class SparseFormat
{
    public const uint SparseHeaderMagic = 0xed26ff3a;

    public const ushort ChunkTypeRaw = 0xCAC1;
    public const ushort ChunkTypeFill = 0xCAC2;
    public const ushort ChunkTypeDontCare = 0xCAC3;
    public const ushort ChunkTypeCrc32 = 0xCAC4;

    public const ushort SparseHeaderSize = 28;
    public const ushort ChunkHeaderSize = 12;

    public const uint MaxChunkDataSize = 64 * 1024 * 1024;
}

public readonly struct SparseHeader
{
    public uint Magic { get; init; }
    public ushort MajorVersion { get; init; }
    public ushort MinorVersion { get; init; }
    public ushort FileHeaderSize { get; init; }
    public ushort ChunkHeaderSize { get; init; }
    public uint BlockSize { get; init; }
    public uint TotalBlocks { get; init; }
    public uint TotalChunks { get; init; }
    public uint ImageChecksum { get; init; }

    public static SparseHeader FromBytes(ReadOnlySpan<byte> data)
    {
        return data.Length < SparseFormat.SparseHeaderSize
            ? throw new ArgumentException("Data length is insufficient to build SparseHeader")
            : new SparseHeader
            {
                Magic = BinaryPrimitives.ReadUInt32LittleEndian(data),
                MajorVersion = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4)),
                MinorVersion = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6)),
                FileHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8)),
                ChunkHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(10)),
                BlockSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12)),
                TotalBlocks = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(16)),
                TotalChunks = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(20)),
                ImageChecksum = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(24))
            };
    }

    public void WriteTo(Span<byte> span)
    {
        if (span.Length < SparseFormat.SparseHeaderSize)
        {
            throw new ArgumentException("Span length is insufficient to write SparseHeader");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4), MajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6), MinorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(8), FileHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(10), ChunkHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), BlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16), TotalBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(20), TotalChunks);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(24), ImageChecksum);
    }

    public byte[] ToBytes()
    {
        var data = new byte[SparseFormat.SparseHeaderSize];
        WriteTo(data);
        return data;
    }

    public bool IsValid()
    {
        return Magic == SparseFormat.SparseHeaderMagic &&
               MajorVersion == 1 &&
               FileHeaderSize >= SparseFormat.SparseHeaderSize &&
               ChunkHeaderSize >= SparseFormat.ChunkHeaderSize &&
               BlockSize > 0 && BlockSize % 4 == 0;
    }
}

public readonly struct ChunkHeader
{
    public ushort ChunkType { get; init; }
    public ushort Reserved { get; init; }
    public uint ChunkSize { get; init; }
    public uint TotalSize { get; init; }

    public static ChunkHeader FromBytes(ReadOnlySpan<byte> data)
    {
        return data.Length < SparseFormat.ChunkHeaderSize
            ? throw new ArgumentException("Data length is insufficient to build ChunkHeader")
            : new ChunkHeader
            {
                ChunkType = BinaryPrimitives.ReadUInt16LittleEndian(data),
                Reserved = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2)),
                ChunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4)),
                TotalSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8))
            };
    }

    public void WriteTo(Span<byte> span)
    {
        if (span.Length < SparseFormat.ChunkHeaderSize)
        {
            throw new ArgumentException("Span length is insufficient to write ChunkHeader");
        }

        BinaryPrimitives.WriteUInt16LittleEndian(span, ChunkType);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2), Reserved);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), ChunkSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), TotalSize);
    }

    public byte[] ToBytes()
    {
        var data = new byte[SparseFormat.ChunkHeaderSize];
        WriteTo(data);
        return data;
    }

    public bool IsValid()
    {
        return ChunkType is SparseFormat.ChunkTypeRaw or
               SparseFormat.ChunkTypeFill or
               SparseFormat.ChunkTypeDontCare or
               SparseFormat.ChunkTypeCrc32;
    }
}
