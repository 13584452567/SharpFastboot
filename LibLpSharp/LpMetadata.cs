namespace LibLpSharp;

public class LpMetadata
{
    public LpMetadataGeometry Geometry;
    public LpMetadataHeader Header;
    public List<LpMetadataPartition> Partitions = [];
    public List<LpMetadataExtent> Extents = [];
    public List<LpMetadataPartitionGroup> Groups = [];
    public List<LpMetadataBlockDevice> BlockDevices = [];
}