using System.Text;

namespace LibLpSharp;

public class Partition(string name, string groupName, uint attributes)
{
    public string Name { get; set; } = name;
    public string GroupName { get; set; } = groupName;
    public uint Attributes { get; set; } = attributes;
    public ulong Size { get; set; }
    public List<LpMetadataExtent> Extents { get; set; } = [];

    public void AddExtent(LpMetadataExtent extent)
    {
        Extents.Add(extent);
        if (extent.TargetType == MetadataFormat.LP_TARGET_TYPE_LINEAR)
        {
            Size += extent.NumSectors * MetadataFormat.LP_SECTOR_SIZE;
        }
    }
}

public class PartitionGroup(string name, ulong maximumSize, uint flags = 0)
{
    public string Name { get; set; } = name;
    public ulong MaximumSize { get; set; } = maximumSize;
    public uint Flags { get; set; } = flags;
}

public class MetadataBuilder
{
    private LpMetadataGeometry _geometry;
    private readonly List<Partition> _partitions = [];
    private List<PartitionGroup> _groups = [];
    private List<LpMetadataBlockDevice> _blockDevices = [];

    public MetadataBuilder()
    {
        _groups.Add(new PartitionGroup("default", 0));
    }

    public static MetadataBuilder New(ulong deviceSize, uint metadataMaxSize, uint metadataSlotCount)
    {
        var builder = new MetadataBuilder();
        builder.Init(deviceSize, metadataMaxSize, metadataSlotCount);
        return builder;
    }

    public static MetadataBuilder FromMetadata(LpMetadata metadata)
    {
        var builder = new MetadataBuilder
        {
            _geometry = metadata.Geometry,
            _blockDevices = [.. metadata.BlockDevices],
            _groups = [.. metadata.Groups.Select(g => new PartitionGroup(g.GetName(), g.MaximumSize, g.Flags))]
        };

        foreach (var p in metadata.Partitions)
        {
            var partition = new Partition(p.GetName(), builder._groups[(int)p.GroupIndex].Name, p.Attributes);
            for (var i = 0; i < p.NumExtents; i++)
            {
                partition.AddExtent(metadata.Extents[(int)(p.FirstExtentIndex + i)]);
            }
            builder._partitions.Add(partition);
        }
        return builder;
    }

    private void Init(ulong deviceSize, uint metadataMaxSize, uint metadataSlotCount)
    {
        _geometry = new LpMetadataGeometry
        {
            Magic = MetadataFormat.LP_METADATA_GEOMETRY_MAGIC,
            StructSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<LpMetadataGeometry>(),
            MetadataMaxSize = metadataMaxSize,
            MetadataSlotCount = metadataSlotCount,
            LogicalBlockSize = 4096
        };

        var super = new LpMetadataBlockDevice
        {
            Alignment = 4096,
            AlignmentOffset = 0,
            Size = deviceSize,
            FirstLogicalSector = Utility.AlignTo(
                MetadataFormat.LP_PARTITION_RESERVED_BYTES + ((MetadataFormat.LP_METADATA_GEOMETRY_SIZE + ((ulong)metadataMaxSize * metadataSlotCount)) * 2),
                4096
            ) / MetadataFormat.LP_SECTOR_SIZE
        };

        var nameBytes = Encoding.ASCII.GetBytes(MetadataFormat.LP_METADATA_DEFAULT_PARTITION_NAME);
        for (var i = 0; i < nameBytes.Length && i < 35; i++)
        {
            super.PartitionName[i] = nameBytes[i];
        }

        _blockDevices.Add(super);
    }

    public void AddPartition(string name, string groupName, uint attributes)
    {
        if (_partitions.Any(p => p.Name == name))
        {
            throw new ArgumentException($"分区 '{name}' 已存在");
        }

        if (!_groups.Any(g => g.Name == groupName))
        {
            throw new ArgumentException($"分区组 '{groupName}' 不存在");
        }

        _partitions.Add(new Partition(name, groupName, attributes));
    }

    public void RemovePartition(string name)
    {
        var partition = FindPartition(name);
        if (partition != null)
        {
            _partitions.Remove(partition);
        }
    }

    public void ReorderPartitions(IEnumerable<string> orderedNames)
    {
        var newOrder = new List<Partition>();
        foreach (var name in orderedNames)
        {
            var p = _partitions.Find(x => x.Name == name);
            if (p != null)
            {
                newOrder.Add(p);
            }
        }
        _partitions.Clear();
        _partitions.AddRange(newOrder);
    }

    public void AddGroup(string name, ulong maxSize)
    {
        if (_groups.Any(g => g.Name == name))
        {
            throw new ArgumentException($"分区组 '{name}' 已存在");
        }

        _groups.Add(new PartitionGroup(name, maxSize));
    }

    public Partition? FindPartition(string name) => _partitions.FirstOrDefault(p => p.Name == name);

    public void ResizeBlockDevice(ulong newSize)
    {
        if (_blockDevices.Count == 0)
        {
            return;
        }

        // 验证新大小是否足够容纳现有的所有 extent
        var maxSectorUsed = _blockDevices[0].FirstLogicalSector;
        foreach (var p in _partitions)
        {
            foreach (var extent in p.Extents)
            {
                if (extent.TargetType == MetadataFormat.LP_TARGET_TYPE_LINEAR)
                {
                    maxSectorUsed = Math.Max(maxSectorUsed, extent.TargetData + extent.NumSectors);
                }
            }
        }

        var minRequiredSize = maxSectorUsed * MetadataFormat.LP_SECTOR_SIZE;
        if (newSize < minRequiredSize)
        {
            throw new InvalidOperationException($"无法调整镜像大小：新容量 ({newSize / (1024 * 1024.0):F2} MiB) 小于分区占用的最小空间 ({minRequiredSize / (1024 * 1024.0):F2} MiB)");
        }

        var device = _blockDevices[0];
        device.Size = newSize;
        _blockDevices[0] = device;
    }

    public void ResizePartition(Partition partition, ulong requestedSize)
    {
        if (partition.Size == requestedSize)
        {
            return;
        }

        if (requestedSize > partition.Size)
        {
            // 检查分区组大小限制
            var group = _groups.First(g => g.Name == partition.GroupName);
            if (group.MaximumSize > 0)
            {
                var currentGroupSize = _partitions
                    .Where(p => p.GroupName == partition.GroupName)
                    .Aggregate(0UL, (sum, p) => sum + p.Size);
                if (currentGroupSize - partition.Size + requestedSize > group.MaximumSize)
                {
                    throw new InvalidOperationException($"超过分区组 '{partition.GroupName}' 的容量限制");
                }
            }
            if (!GrowPartition(partition, requestedSize))
            {
                throw new InvalidOperationException($"磁盘空间不足，无法扩容分区 '{partition.Name}'");
            }
            return;
        }

        ShrinkPartition(partition, requestedSize);
    }

    private void ShrinkPartition(Partition partition, ulong requestedSize)
    {
        var sectorsToKeep = requestedSize / MetadataFormat.LP_SECTOR_SIZE;
        ulong currentSectors = 0;
        var newExtents = new List<LpMetadataExtent>();

        foreach (var extent in partition.Extents)
        {
            if (currentSectors + extent.NumSectors <= sectorsToKeep)
            {
                newExtents.Add(extent);
                currentSectors += extent.NumSectors;
            }
            else
            {
                var needed = sectorsToKeep - currentSectors;
                if (needed > 0)
                {
                    var partial = extent;
                    partial.NumSectors = needed;
                    newExtents.Add(partial);
                    currentSectors += needed;
                }
                break;
            }
        }
        partition.Extents = newExtents;
        partition.Size = currentSectors * MetadataFormat.LP_SECTOR_SIZE;
    }

    private bool GrowPartition(Partition partition, ulong requestedSize)
    {
        var sectorsNeeded = (requestedSize - partition.Size) / MetadataFormat.LP_SECTOR_SIZE;
        var freeRegions = GetFreeRegions();

        var device = _blockDevices[0];
        var alignmentSectors = device.Alignment / MetadataFormat.LP_SECTOR_SIZE;
        var alignmentOffsetSectors = device.AlignmentOffset / MetadataFormat.LP_SECTOR_SIZE;

        foreach (var region in freeRegions)
        {
            if (sectorsNeeded == 0)
            {
                break;
            }

            var startSector = region.StartSector;
            if (alignmentSectors > 0)
            {
                var remainder = (startSector - alignmentOffsetSectors) % alignmentSectors;
                if (remainder > 0)
                {
                    startSector += alignmentSectors - remainder;
                }
            }

            if (startSector >= region.StartSector + region.NumSectors)
            {
                continue;
            }

            var availableSectors = region.StartSector + region.NumSectors - startSector;
            var allocateSectors = Math.Min(availableSectors, sectorsNeeded);

            partition.AddExtent(new LpMetadataExtent
            {
                NumSectors = allocateSectors,
                TargetType = MetadataFormat.LP_TARGET_TYPE_LINEAR,
                TargetData = startSector,
                TargetSource = 0
            });
            sectorsNeeded -= allocateSectors;
        }

        return sectorsNeeded == 0;
    }

    private struct FreeRegion
    {
        public ulong StartSector;
        public ulong NumSectors;
    }

    private List<FreeRegion> GetFreeRegions()
    {
        var regions = new List<FreeRegion>();
        var firstSector = _blockDevices[0].FirstLogicalSector;
        var lastSector = _blockDevices[0].Size / MetadataFormat.LP_SECTOR_SIZE;

        var extents = _partitions.SelectMany(p => p.Extents)
            .Where(e => e.TargetType == MetadataFormat.LP_TARGET_TYPE_LINEAR)
            .OrderBy(e => e.TargetData)
            .ToList();

        var currentSector = firstSector;
        foreach (var extent in extents)
        {
            if (extent.TargetData > currentSector)
            {
                regions.Add(new FreeRegion { StartSector = currentSector, NumSectors = extent.TargetData - currentSector });
            }
            currentSector = Math.Max(currentSector, extent.TargetData + extent.NumSectors);
        }

        if (currentSector < lastSector)
        {
            regions.Add(new FreeRegion { StartSector = currentSector, NumSectors = lastSector - currentSector });
        }

        return regions;
    }

    public LpMetadata Export()
    {
        var metadata = new LpMetadata
        {
            Geometry = _geometry,
            Header = new LpMetadataHeader
            {
                Magic = MetadataFormat.LP_METADATA_HEADER_MAGIC,
                MajorVersion = MetadataFormat.LP_METADATA_MAJOR_VERSION,
                MinorVersion = MetadataFormat.LP_METADATA_MINOR_VERSION_MIN,
                HeaderSize = (uint)System.Runtime.CompilerServices.Unsafe.SizeOf<LpMetadataHeader>()
            },
            Groups = [.. _groups.Select(g =>
                {
                    var group = new LpMetadataPartitionGroup { Flags = g.Flags, MaximumSize = g.MaximumSize };
                    var nameBytes = Encoding.ASCII.GetBytes(g.Name);
                    for (var i = 0; i < nameBytes.Length && i < 35; i++)
                    {
                        group.Name[i] = nameBytes[i];
                    }
                    return group;
                })],

            BlockDevices = _blockDevices
        };

        foreach (var p in _partitions)
        {
            var lpp = new LpMetadataPartition
            {
                Attributes = p.Attributes,
                FirstExtentIndex = (uint)metadata.Extents.Count,
                NumExtents = (uint)p.Extents.Count,
                GroupIndex = (uint)_groups.FindIndex(g => g.Name == p.GroupName)
            };
            var nameBytes = Encoding.ASCII.GetBytes(p.Name);
            for (var i = 0; i < nameBytes.Length && i < 35; i++)
            {
                lpp.Name[i] = nameBytes[i];
            }
            metadata.Partitions.Add(lpp);
            metadata.Extents.AddRange(p.Extents);
        }

        return metadata;
    }
}