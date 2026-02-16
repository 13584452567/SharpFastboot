using System.Runtime.InteropServices;
using System.Text;

namespace LibLpSharp;

[System.Runtime.CompilerServices.InlineArray(32)]
public struct Buffer32 { private byte _element0; }

[System.Runtime.CompilerServices.InlineArray(36)]
public struct Buffer36 { private byte _element0; }

[System.Runtime.CompilerServices.InlineArray(124)]
public struct Buffer124 { private byte _element0; }

public static class MetadataFormat
{
    /* LpMetadataGeometry 的魔数 */
    public const uint LP_METADATA_GEOMETRY_MAGIC = 0x616c4467;

    /* 为几何信息预留的空间大小 */
    public const int LP_METADATA_GEOMETRY_SIZE = 4096;

    /* LpMetadataHeader 的魔数 */
    public const uint LP_METADATA_HEADER_MAGIC = 0x414C5030;

    /* 当前元数据版本 */
    public const ushort LP_METADATA_MAJOR_VERSION = 10;
    public const ushort LP_METADATA_MINOR_VERSION_MIN = 0;
    public const ushort LP_METADATA_MINOR_VERSION_MAX = 2;

    public const int LP_SECTOR_SIZE = 512;
    public const int LP_PARTITION_RESERVED_BYTES = 4096;

    public const string LP_METADATA_DEFAULT_PARTITION_NAME = "super";

    /* LpMetadataPartition::attributes 字段的属性 */
    public const uint LP_PARTITION_ATTR_NONE = 0x0;
    public const uint LP_PARTITION_ATTR_READONLY = 1 << 0;
    public const uint LP_PARTITION_ATTR_SLOT_SUFFIXED = 1 << 1;
    public const uint LP_PARTITION_ATTR_UPDATED = 1 << 2;
    public const uint LP_PARTITION_ATTR_DISABLED = 1 << 3;

    /* LpMetadataExtent 的目标类型 */
    public const uint LP_TARGET_TYPE_LINEAR = 0;
    public const uint LP_TARGET_TYPE_ZERO = 1;

    /* LpMetadataPartitionGroup 的标志 */
    public const uint LP_GROUP_SLOT_SUFFIXED = 1 << 0;

    /* LpMetadataBlockDevice 的标志 */
    public const uint LP_BLOCK_DEVICE_SLOT_SUFFIXED = 1 << 0;

    /* 头部标志 */
    public const uint LP_HEADER_FLAG_VIRTUAL_AB_DEVICE = 0x1;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LpMetadataGeometry
{
    public uint Magic;
    public uint StructSize;
    public Buffer32 Checksum;
    public uint MetadataMaxSize;
    public uint MetadataSlotCount;
    public uint LogicalBlockSize;

    public static LpMetadataGeometry FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < System.Runtime.CompilerServices.Unsafe.SizeOf<LpMetadataGeometry>())
        {
            throw new ArgumentException("Data too small for LpMetadataGeometry");
        }

        return MemoryMarshal.Read<LpMetadataGeometry>(data);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LpMetadataTableDescriptor
{
    public uint Offset;
    public uint NumEntries;
    public uint EntrySize;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LpMetadataHeader
{
    public uint Magic;
    public ushort MajorVersion;
    public ushort MinorVersion;
    public uint HeaderSize;
    public Buffer32 HeaderChecksum;
    public uint TablesSize;
    public Buffer32 TablesChecksum;

    public LpMetadataTableDescriptor Partitions;
    public LpMetadataTableDescriptor Extents;
    public LpMetadataTableDescriptor Groups;
    public LpMetadataTableDescriptor BlockDevices;

    public uint Flags;
    public Buffer124 Reserved;

    public static LpMetadataHeader FromBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length < System.Runtime.CompilerServices.Unsafe.SizeOf<LpMetadataHeader>())
        {
            throw new ArgumentException("Data too small for LpMetadataHeader");
        }

        return MemoryMarshal.Read<LpMetadataHeader>(data);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LpMetadataPartition
{
    public Buffer36 Name;
    public uint Attributes;
    public uint FirstExtentIndex;
    public uint NumExtents;
    public uint GroupIndex;

    public string GetName()
    {
        ReadOnlySpan<byte> nameSpan = Name;
        var len = 0;
        while (len < nameSpan.Length && nameSpan[len] != 0)
        {
            len++;
        }

        return Encoding.ASCII.GetString(nameSpan[..len]);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LpMetadataExtent
{
    public ulong NumSectors;
    public uint TargetType;
    public ulong TargetData;
    public uint TargetSource;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LpMetadataPartitionGroup
{
    public Buffer36 Name;
    public uint Flags;
    public ulong MaximumSize;

    public string GetName()
    {
        ReadOnlySpan<byte> nameSpan = Name;
        var len = 0;
        while (len < nameSpan.Length && nameSpan[len] != 0)
        {
            len++;
        }

        return Encoding.ASCII.GetString(nameSpan[..len]);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct LpMetadataBlockDevice
{
    public ulong FirstLogicalSector;
    public uint Alignment;
    public uint AlignmentOffset;
    public ulong Size;
    public Buffer36 PartitionName;
    public uint Flags;

    public string GetPartitionName()
    {
        ReadOnlySpan<byte> nameSpan = PartitionName;
        var len = 0;
        while (len < nameSpan.Length && nameSpan[len] != 0)
        {
            len++;
        }

        return Encoding.ASCII.GetString(nameSpan[..len]);
    }
}