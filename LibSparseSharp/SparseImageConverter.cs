using LibLpSharp;

namespace LibSparseSharp;

/// <summary>
/// Sparse image converter
/// </summary>
public static class SparseImageConverter
{

    /// <summary>
    /// Converts sparse images to raw images
    /// </summary>
    public static void ConvertSparseToRaw(string[] inputFiles, string outputFile)
    {
        using var outputStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write);
        SparseFileNativeHelper.MarkAsSparse(outputStream);
        long maxFileSize = 0;
        foreach (var inputFile in inputFiles)
        {
            var tempSparseFile = SparseFile.FromImageFile(inputFile);
            var fileSize = (long)tempSparseFile.Header.TotalBlocks * tempSparseFile.Header.BlockSize;
            maxFileSize = Math.Max(maxFileSize, fileSize);
            tempSparseFile.Dispose();
        }
        outputStream.SetLength(maxFileSize);
        foreach (var inputFile in inputFiles)
        {
            var sparseFile = SparseFile.FromImageFile(inputFile);
            WriteRawImageFromSparse(sparseFile, outputStream);
            sparseFile.Dispose();
        }
    }

    /// <summary>
    /// Converts raw images to sparse images
    /// </summary>
    public static void ConvertRawToSparse(string inputFile, string outputFile, uint blockSize = 4096)
    {
        using var outputStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write);
        using var sparseFile = SparseFile.FromRawFile(inputFile, blockSize);
        sparseFile.WriteToStream(outputStream);
    }

    /// <summary>
    /// Creates a new Super image and saves it in Sparse format
    /// </summary>
    public static void CreateSuperSparse(
        string outputFile,
        ulong deviceSize,
        uint metadataMaxSize,
        uint metadataSlotCount,
        IDictionary<string, (ulong Size, string? ImagePath)> partitions,
        IDictionary<string, ulong>? groups = null)
    {
        var builder = new SuperImageBuilder(deviceSize, metadataMaxSize, metadataSlotCount);

        if (groups != null)
        {
            foreach (var group in groups)
            {
                builder.AddGroup(group.Key, group.Value);
            }
        }

        foreach (var p in partitions)
        {
            builder.AddPartition(p.Key, p.Value.Size, "default", MetadataFormat.LP_PARTITION_ATTR_READONLY, p.Value.ImagePath);
        }

        using var sparseFile = builder.Build();
        using var outputStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write);
        sparseFile.WriteToStream(outputStream);
    }

    /// <summary>
    /// Unpacks Super images (supports both Sparse and Raw formats)
    /// </summary>
    public static void UnpackSuper(string inputFile, string outputDir)
    {
        if (!Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        using var fs = new FileStream(inputFile, FileMode.Open, FileAccess.Read);
        Stream superStream = fs;

        // Check if it's in Sparse format
        var magicBuf = new byte[4];
        fs.ReadExactly(magicBuf, 0, 4);
        fs.Seek(0, SeekOrigin.Begin);

        SparseFile? sparseFile = null;
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(magicBuf) == SparseFormat.SparseHeaderMagic)
        {
            sparseFile = SparseFile.FromStream(fs);
            superStream = new SparseStream(sparseFile);
        }

        try
        {
            var metadata = MetadataReader.ReadFromImageStream(superStream);
            foreach (var partition in metadata.Partitions)
            {
                var name = partition.GetName();
                var outputPath = Path.Combine(outputDir, $"{name}.img");

                // 1. Calculate total size and pre-set length
                ulong totalSectors = 0;
                for (var i = 0; i < partition.NumExtents; i++)
                {
                    totalSectors += metadata.Extents[(int)(partition.FirstExtentIndex + i)].NumSectors;
                }

                var totalSize = (long)totalSectors * MetadataFormat.LP_SECTOR_SIZE;

                using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                SparseFileNativeHelper.MarkAsSparse(outFs); // Mark as sparse file for hole optimization
                outFs.SetLength(totalSize);

                long currentOutOffset = 0;
                for (var i = 0; i < partition.NumExtents; i++)
                {
                    var extent = metadata.Extents[(int)(partition.FirstExtentIndex + i)];
                    var size = (long)extent.NumSectors * MetadataFormat.LP_SECTOR_SIZE;

                    if (extent.TargetType == MetadataFormat.LP_TARGET_TYPE_LINEAR)
                    {
                        var offset = (long)extent.TargetData * MetadataFormat.LP_SECTOR_SIZE;
                        superStream.Seek(offset, SeekOrigin.Begin);
                        outFs.Seek(currentOutOffset, SeekOrigin.Begin); // Seek to the exact position in output file
                        CopyStreamPart(superStream, outFs, size);
                    }
                    // For LP_TARGET_TYPE_ZERO, since SetLength was called and it's marked as Sparse,
                    // we just need to skip it (increment currentOutOffset), the file system won't allocate actual space.

                    currentOutOffset += size;
                }
            }
        }
        finally
        {
            if (sparseFile != null)
            {
                superStream.Dispose();
                sparseFile.Dispose();
            }
        }
    }

    private static void CopyStreamPart(Stream input, Stream output, long length)
    {
        var buffer = new byte[1024 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = input.Read(buffer, 0, toRead);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    /// <summary>
    /// Writes raw image data from a sparse file
    /// </summary>
    private static void WriteRawImageFromSparse(SparseFile sparseFile, Stream outputStream)
    {
        // Migrated to SparseFile internal implementation; call directly with sparseMode=true to support Seek skipping
        if (outputStream.CanSeek)
        {
            outputStream.Seek(0, SeekOrigin.Begin);
        }
        sparseFile.WriteRawToStream(outputStream, true);
    }

    /// <summary>
    /// Splits a large sparse image into multiple images of specified size
    /// </summary>
    public static void ResparseImage(string inputFile, string outputPattern, long maxFileSize)
    {
        using var stream = new FileStream(inputFile, FileMode.Open, FileAccess.Read);
        var sparseFile = SparseFile.FromStream(stream);
        var files = sparseFile.Resparse(maxFileSize);

        for (var i = 0; i < files.Count; i++)
        {
            var outPath = outputPattern.Contains("{0}")
                ? string.Format(outputPattern, i)
                : $"{outputPattern}.{i:D2}";

            using var outStream = new FileStream(outPath, FileMode.Create, FileAccess.Write);
            files[i].WriteToStream(outStream, true); // Split images usually need to include CRC
        }
    }
}
