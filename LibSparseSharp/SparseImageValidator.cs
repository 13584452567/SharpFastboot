namespace LibSparseSharp;

/// <summary>
/// Sparse image validator
/// </summary>
public static class SparseImageValidator
{
    /// <summary>
    /// Validates a sparse image file
    /// </summary>
    public static ValidationResult ValidateSparseImage(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        var sparseFile = SparseFile.FromStream(stream);

        var result = new ValidationResult
        {
            Success = true,
            FilePath = filePath,
            Header = new HeaderInfo
            {
                Magic = sparseFile.Header.Magic,
                Version = $"{sparseFile.Header.MajorVersion}.{sparseFile.Header.MinorVersion}",
                BlockSize = sparseFile.Header.BlockSize,
                TotalBlocks = sparseFile.Header.TotalBlocks,
                TotalChunks = sparseFile.Header.TotalChunks
            }
        };
        if (!sparseFile.Header.IsValid())
        {
            throw new InvalidDataException("Invalid sparse file header");
        }
        uint totalBlocks = 0;
        var chunkInfos = new List<ChunkInfo>();

        for (uint i = 0; i < sparseFile.Header.TotalChunks; i++)
        {
            var chunk = sparseFile.Chunks[(int)i];

            if (!chunk.Header.IsValid())
            {
                throw new InvalidDataException($"Invalid chunk header at index {i}");
            }

            var chunkInfo = new ChunkInfo
            {
                Index = i,
                ChunkType = chunk.Header.ChunkType,
                ChunkSize = chunk.Header.ChunkSize,
                TotalSize = chunk.Header.TotalSize
            };
            chunkInfos.Add(chunkInfo);

            totalBlocks += chunk.Header.ChunkSize;
        }

        result.Chunks = chunkInfos;
        if (totalBlocks > sparseFile.Header.TotalBlocks)
        {
            throw new InvalidDataException($"Total blocks in chunks ({totalBlocks}) exceeds total blocks in header ({sparseFile.Header.TotalBlocks})");
        }

        result.CalculatedTotalBlocks = totalBlocks;
        return result;
    }

    /// <summary>
    /// Validation result
    /// </summary>
    public class ValidationResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? FilePath { get; set; }
        public HeaderInfo? Header { get; set; }
        public List<ChunkInfo>? Chunks { get; set; }
        public uint CalculatedTotalBlocks { get; set; }
    }

    /// <summary>
    /// Header information
    /// </summary>
    public class HeaderInfo
    {
        public uint Magic { get; set; }
        public string Version { get; set; } = "";
        public uint BlockSize { get; set; }
        public uint TotalBlocks { get; set; }
        public uint TotalChunks { get; set; }
    }

    /// <summary>
    /// Chunk information
    /// </summary>
    public class ChunkInfo
    {
        public uint Index { get; set; }
        public ushort ChunkType { get; set; }
        public uint ChunkSize { get; set; }
        public uint TotalSize { get; set; }
    }

    /// <summary>
    /// Checks if the file is a sparse image
    /// </summary>
    public static bool IsSparseImage(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            var magicBytes = new byte[4];
            if (stream.Read(magicBytes, 0, 4) != 4)
            {
                return false;
            }

            var magic = BitConverter.ToUInt32(magicBytes, 0);
            return magic == SparseFormat.SparseHeaderMagic;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets detailed information about a sparse image file
    /// </summary>
    public static SparseImageInfo GetSparseImageInfo(string filePath)
    {
        if (!IsSparseImage(filePath))
        {
            throw new InvalidDataException("Not a valid sparse image file");
        }

        var header = SparseFile.PeekHeader(filePath);
        var fileInfo = new FileInfo(filePath);
        var uncompressedSize = (long)header.TotalBlocks * header.BlockSize;
        var compressionRatio = 100.0 - ((double)fileInfo.Length / uncompressedSize * 100.0);

        return new SparseImageInfo
        {
            Success = true,
            FilePath = filePath,
            FileSize = fileInfo.Length,
            UncompressedSize = uncompressedSize,
            CompressionRatio = compressionRatio,
            Version = $"{header.MajorVersion}.{header.MinorVersion}",
            BlockSize = header.BlockSize,
            TotalBlocks = header.TotalBlocks,
            TotalChunks = header.TotalChunks
        };
    }

    /// <summary>
    /// Detailed Sparse image information
    /// </summary>
    public class SparseImageInfo
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? FilePath { get; set; }
        public long FileSize { get; set; }
        public long UncompressedSize { get; set; }
        public double CompressionRatio { get; set; }
        public string Version { get; set; } = "";
        public uint BlockSize { get; set; }
        public uint TotalBlocks { get; set; }
        public uint TotalChunks { get; set; }
    }
}
