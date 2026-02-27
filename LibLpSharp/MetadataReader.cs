using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LibLpSharp;

public static class MetadataReader
{
    public static LpMetadata ReadFromImageFile(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadFromImageStream(stream);
    }

    public static LpMetadata ReadFromImageStream(Stream stream)
    {
        // 依次尝试可能的几何数据偏移：标准 (4096), 备份 (8192), 以及 0
        long[] tryOffsets = [ MetadataFormat.LP_PARTITION_RESERVED_BYTES,
                              MetadataFormat.LP_PARTITION_RESERVED_BYTES + MetadataFormat.LP_METADATA_GEOMETRY_SIZE,
                              0 ];

        foreach (var offset in tryOffsets)
        {
            try
            {
                LpLogger.Info($"正在尝试从偏移量 {offset} 读取几何信息...");
                var buffer = new byte[MetadataFormat.LP_METADATA_GEOMETRY_SIZE];
                stream.Seek(offset, SeekOrigin.Begin);
                if (stream.Read(buffer, 0, buffer.Length) == buffer.Length)
                {
                    ParseGeometry(buffer, out var geometry);
                    // 找到有效的几何数据，根据读取位置（主/备）计算下层元数据基址偏移
                    var metadataOffset = offset;
                    if (offset == MetadataFormat.LP_PARTITION_RESERVED_BYTES + MetadataFormat.LP_METADATA_GEOMETRY_SIZE)
                    {
                        // 正在从备份几何位置读取，由于 LibLp 总从对应主几何后寻找元数据，此处需向前回推到主几何位置
                        metadataOffset -= MetadataFormat.LP_METADATA_GEOMETRY_SIZE;
                    }

                    stream.Seek(metadataOffset + (MetadataFormat.LP_METADATA_GEOMETRY_SIZE * 2), SeekOrigin.Begin);
                    var metadata = ParseMetadata(geometry, stream);
                    LpLogger.Info($"成功解析元数据: 分区数={metadata.Partitions.Count}, 组数={metadata.Groups.Count}");
                    return metadata;
                }
            }
            catch (Exception ex)
            {
                LpLogger.Warning($"偏移量 {offset} 解析失败: {ex.Message}");
                continue;
            }
        }

        throw new InvalidDataException("无法找到有效的 LpMetadataGeometry。镜像可能不是 super 镜像或已损坏。");
    }

    public static void ParseGeometry(ReadOnlySpan<byte> buffer, out LpMetadataGeometry geometry)
    {
        geometry = default;
        if (buffer.Length < System.Runtime.CompilerServices.Unsafe.SizeOf<LpMetadataGeometry>())
        {
            throw new ArgumentException("数据长度不足以解析 LpMetadataGeometry");
        }

        geometry = LpMetadataGeometry.FromBytes(buffer);

        if (geometry.Magic != MetadataFormat.LP_METADATA_GEOMETRY_MAGIC)
        {
            throw new InvalidDataException($"无效的 LpMetadataGeometry 魔数: 0x{geometry.Magic:X8} (期望: 0x{MetadataFormat.LP_METADATA_GEOMETRY_MAGIC:X8})");
        }

        if (geometry.StructSize > (uint)buffer.Length)
        {
            throw new InvalidDataException($"LpMetadataGeometry 结构大小超出缓冲区: {geometry.StructSize} > {buffer.Length}");
        }

        // Verify checksum
        ReadOnlySpan<byte> originalChecksum = geometry.Checksum;

        // Zero out checksum before calculation
        var tempBuffer = buffer[..(int)geometry.StructSize].ToArray();
        // Checksum is at offset 8, length 32
        for (var i = 0; i < 32; i++)
        {
            tempBuffer[8 + i] = 0;
        }

        using var sha256 = SHA256.Create();
        var computed = sha256.ComputeHash(tempBuffer);
        for (var i = 0; i < 32; i++)
        {
            if (computed[i] != originalChecksum[i])
            {
                throw new InvalidDataException("LpMetadataGeometry 校验和不匹配");
            }
        }
    }

    public static LpMetadata ParseMetadata(LpMetadataGeometry geometry, Stream stream)
    {
        var headerBuffer = new byte[System.Runtime.CompilerServices.Unsafe.SizeOf<LpMetadataHeader>()];
        if (stream.Read(headerBuffer, 0, headerBuffer.Length) != headerBuffer.Length)
        {
            throw new InvalidDataException("无法读取 LpMetadataHeader");
        }

        var header = LpMetadataHeader.FromBytes(headerBuffer);
        if (header.Magic != MetadataFormat.LP_METADATA_HEADER_MAGIC)
        {
            throw new InvalidDataException("无效的 LpMetadataHeader 魔数");
        }

        // Verify header checksum
        ReadOnlySpan<byte> originalHeaderChecksum = header.HeaderChecksum;

        var headerCopy = (byte[])headerBuffer.Clone();
        // Checksum is at offset 12, zero it before calculation
        for (var i = 0; i < 32; i++)
        {
            headerCopy[12 + i] = 0;
        }

        using var sha256 = SHA256.Create();
        var computedHeader = sha256.ComputeHash(headerCopy, 0, (int)header.HeaderSize);
        for (var i = 0; i < 32; i++)
        {
            if (computedHeader[i] != originalHeaderChecksum[i])
            {
                throw new InvalidDataException("LpMetadataHeader 校验和不匹配");
            }
        }

        // Read tables
        var tablesBuffer = new byte[header.TablesSize];
        if (stream.Read(tablesBuffer, 0, tablesBuffer.Length) != tablesBuffer.Length)
        {
            throw new InvalidDataException("无法读取元数据表 (Metadata Tables)");
        }

        // Verify tables checksum
        ReadOnlySpan<byte> originalTablesChecksum = header.TablesChecksum;

        var computedTables = sha256.ComputeHash(tablesBuffer);
        for (var i = 0; i < 32; i++)
        {
            if (computedTables[i] != originalTablesChecksum[i])
            {
                throw new InvalidDataException("元数据表校验和不匹配");
            }
        }

        var metadata = new LpMetadata
        {
            Geometry = geometry,
            Header = header
        };

        ParseTable(tablesBuffer, header.Partitions, metadata.Partitions);
        ParseTable(tablesBuffer, header.Extents, metadata.Extents);
        ParseTable(tablesBuffer, header.Groups, metadata.Groups);
        ParseTable(tablesBuffer, header.BlockDevices, metadata.BlockDevices);

        return metadata;
    }

    public static LpMetadata ReadMetadata(Stream stream, uint slotNumber)
    {
        ReadLogicalPartitionGeometry(stream, out var geometry);
        return ReadPrimaryMetadata(stream, geometry, slotNumber);
    }

    public static void ReadLogicalPartitionGeometry(Stream stream, out LpMetadataGeometry geometry)
    {
        try
        {
            ReadPrimaryGeometry(stream, out geometry);
        }
        catch
        {
            ReadBackupGeometry(stream, out geometry);
        }
    }

    public static void ReadPrimaryGeometry(Stream stream, out LpMetadataGeometry geometry) => ReadGeometry(stream, MetadataFormat.LP_PARTITION_RESERVED_BYTES, out geometry);

    public static void ReadBackupGeometry(Stream stream, out LpMetadataGeometry geometry) => ReadGeometry(stream, MetadataFormat.LP_PARTITION_RESERVED_BYTES + MetadataFormat.LP_METADATA_GEOMETRY_SIZE, out geometry);

    private static void ReadGeometry(Stream stream, long offset, out LpMetadataGeometry geometry)
    {
        var buffer = new byte[MetadataFormat.LP_METADATA_GEOMETRY_SIZE];
        stream.Seek(offset, SeekOrigin.Begin);
        if (stream.Read(buffer, 0, buffer.Length) != buffer.Length)
        {
            throw new InvalidDataException($"无法在偏移 0x{offset:X} 处读取几何数据");
        }
        ParseGeometry(buffer, out geometry);
    }

    public static LpMetadata ReadPrimaryMetadata(Stream stream, LpMetadataGeometry geometry, uint slotNumber)
    {
        var offset = GetPrimaryMetadataOffset(geometry, slotNumber);
        stream.Seek(offset, SeekOrigin.Begin);
        return ParseMetadata(geometry, stream);
    }

    public static long GetPrimaryMetadataOffset(LpMetadataGeometry geometry, uint slotNumber) => MetadataFormat.LP_PARTITION_RESERVED_BYTES + (MetadataFormat.LP_METADATA_GEOMETRY_SIZE * 2) + ((long)slotNumber * geometry.MetadataMaxSize);

    public static long GetBackupMetadataOffset(LpMetadataGeometry geometry, uint slotNumber)
    {
        var start = MetadataFormat.LP_PARTITION_RESERVED_BYTES + (MetadataFormat.LP_METADATA_GEOMETRY_SIZE * 2) + ((long)geometry.MetadataMaxSize * geometry.MetadataSlotCount);
        return start + ((long)slotNumber * geometry.MetadataMaxSize);
    }

    private static void ParseTable<T>(byte[] buffer, LpMetadataTableDescriptor desc, List<T> list) where T : unmanaged
    {
        if (desc.NumEntries == 0)
        {
            return;
        }

        for (uint i = 0; i < desc.NumEntries; i++)
        {
            var offset = (int)(desc.Offset + (i * desc.EntrySize));
            list.Add(MemoryMarshal.Read<T>(buffer.AsSpan(offset, (int)desc.EntrySize)));
        }
    }
}