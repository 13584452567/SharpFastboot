namespace LibSparseSharp;

public class SparseChunk(ChunkHeader header) : IDisposable
{
    public uint StartBlock { get; set; } = 0;
    public ChunkHeader Header { get; set; } = header;
    public ISparseDataProvider? DataProvider { get; set; }
    public uint FillValue { get; set; }

    public void Dispose() => DataProvider?.Dispose();
}
