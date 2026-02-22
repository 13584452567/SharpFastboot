using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LibLpSharp;

public static class MetadataWriter
{
    public static byte[] SerializeGeometry(LpMetadataGeometry geometry)
    {
        geometry.Magic = MetadataFormat.LP_METADATA_GEOMETRY_MAGIC;
        geometry.StructSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<LpMetadataGeometry>();

        for (var i = 0; i < 32; i++)
        {
            geometry.Checksum[i] = 0;
        }

        var blob = new byte[System.Runtime.CompilerServices.Unsafe.SizeOf<LpMetadataGeometry>()];
        MemoryMarshal.Write(blob, in geometry);

        using (var sha256 = SHA256.Create())
        {
            var hash = sha256.ComputeHash(blob);
            for (var i = 0; i < 32; i++)
            {
                blob[8 + i] = hash[i];
            }
        }

        var padded = new byte[MetadataFormat.LP_METADATA_GEOMETRY_SIZE];
        Array.Copy(blob, padded, blob.Length);
        return padded;
    }

    public static byte[] SerializeMetadata(LpMetadata metadata)
    {
        var header = metadata.Header;

        var partitions = TableToBytes(metadata.Partitions);
        var extents = TableToBytes(metadata.Extents);
        var groups = TableToBytes(metadata.Groups);
        var blockDevices = TableToBytes(metadata.BlockDevices);

        header.Partitions.Offset = 0;
        header.Partitions.NumEntries = (uint)metadata.Partitions.Count;
        header.Partitions.EntrySize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<LpMetadataPartition>();

        header.Extents.Offset = (uint)partitions.Length;
        header.Extents.NumEntries = (uint)metadata.Extents.Count;
        header.Extents.EntrySize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<LpMetadataExtent>();

        header.Groups.Offset = header.Extents.Offset + (uint)extents.Length;
        header.Groups.NumEntries = (uint)metadata.Groups.Count;
        header.Groups.EntrySize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<LpMetadataPartitionGroup>();

        header.BlockDevices.Offset = header.Groups.Offset + (uint)groups.Length;
        header.BlockDevices.NumEntries = (uint)metadata.BlockDevices.Count;
        header.BlockDevices.EntrySize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<LpMetadataBlockDevice>();

        header.TablesSize = header.BlockDevices.Offset + (uint)blockDevices.Length;

        var tables = new byte[header.TablesSize];
        Array.Copy(partitions, 0, tables, (int)header.Partitions.Offset, partitions.Length);
        Array.Copy(extents, 0, tables, (int)header.Extents.Offset, extents.Length);
        Array.Copy(groups, 0, tables, (int)header.Groups.Offset, groups.Length);
        Array.Copy(blockDevices, 0, tables, (int)header.BlockDevices.Offset, blockDevices.Length);

        using var sha256 = SHA256.Create();
        var tableHash = sha256.ComputeHash(tables);
        for (var i = 0; i < 32; i++)
        {
            header.TablesChecksum[i] = tableHash[i];
        }

        header.Magic = MetadataFormat.LP_METADATA_HEADER_MAGIC;
        header.HeaderSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<LpMetadataHeader>();

        for (var i = 0; i < 32; i++)
        {
            header.HeaderChecksum[i] = 0;
        }

        var headerBytes = new byte[header.HeaderSize];
        MemoryMarshal.Write(headerBytes, in header);

        var headerHash = sha256.ComputeHash(headerBytes);
        for (var i = 0; i < 32; i++)
        {
            headerBytes[12 + i] = headerHash[i];
        }

        var result = new byte[headerBytes.Length + tables.Length];
        Array.Copy(headerBytes, 0, result, 0, headerBytes.Length);
        Array.Copy(tables, 0, result, headerBytes.Length, tables.Length);
        return result;
    }

    private static byte[] TableToBytes<T>(List<T> list) where T : unmanaged
    {
        if (list.Count == 0)
        {
            return [];
        }

        var entrySize = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        var result = new byte[list.Count * entrySize];
        for (var i = 0; i < list.Count; i++)
        {
            var entry = list[i];
            MemoryMarshal.Write(result.AsSpan(i * entrySize), in entry);
        }
        return result;
    }

    public static bool WriteToImageFile(string path, LpMetadata metadata)
    {
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite);
        WriteToImageStream(stream, metadata);
        return true;
    }

    public static void WriteToImageStream(Stream stream, LpMetadata metadata)
    {
        var geometryBlob = SerializeGeometry(metadata.Geometry);
        var metadataBlob = SerializeMetadata(metadata);

        if (metadataBlob.Length > metadata.Geometry.MetadataMaxSize)
        {
            throw new InvalidOperationException("序列化后的元数据大小超过了几何数据中定义的 MetadataMaxSize");
        }

        // 写入主几何块及其备份
        stream.Seek(MetadataFormat.LP_PARTITION_RESERVED_BYTES, SeekOrigin.Begin);
        stream.Write(geometryBlob, 0, geometryBlob.Length);
        
        stream.Seek(MetadataFormat.LP_PARTITION_RESERVED_BYTES + MetadataFormat.LP_METADATA_GEOMETRY_SIZE, SeekOrigin.Begin);
        stream.Write(geometryBlob, 0, geometryBlob.Length);

        for (uint i = 0; i < metadata.Geometry.MetadataSlotCount; i++)
        {
            var primaryOffset = MetadataReader.GetPrimaryMetadataOffset(metadata.Geometry, i);
            stream.Seek(primaryOffset, SeekOrigin.Begin);
            stream.Write(metadataBlob, 0, metadataBlob.Length);

            var backupOffset = MetadataReader.GetBackupMetadataOffset(metadata.Geometry, i);
            stream.Seek(backupOffset, SeekOrigin.Begin);
            stream.Write(metadataBlob, 0, metadataBlob.Length);
        }
    }
}