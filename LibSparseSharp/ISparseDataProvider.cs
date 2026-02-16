namespace LibSparseSharp;

public interface ISparseDataProvider : IDisposable
{
    long Length { get; }
    void WriteTo(Stream stream);
    int Read(long offset, byte[] buffer, int bufferOffset, int count);
    ISparseDataProvider GetSubProvider(long offset, long length);
}
