namespace LibSparseSharp;

/// <summary>
/// Sparse image utility tools
/// </summary>
public static class SparseImageUtils
{
    /// <summary>
    /// Gets detailed information about a file
    /// </summary>
    public static FileInfoResult GetFileInfo(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        var fileInfo = new FileInfo(filePath);
        var result = new FileInfoResult
        {
            Success = true,
            FilePath = filePath,
            FileSize = fileInfo.Length,
            IsSparseImage = SparseImageValidator.IsSparseImage(filePath)
        };

        if (result.IsSparseImage)
        {
            var header = SparseFile.PeekHeader(filePath);
            result.SparseInfo = new SparseFileInfo
            {
                Version = $"{header.MajorVersion}.{header.MinorVersion}",
                BlockSize = header.BlockSize,
                TotalBlocks = header.TotalBlocks,
                TotalChunks = header.TotalChunks,
                UncompressedSize = (long)header.TotalBlocks * header.BlockSize
            };
        }

        return result;
    }

    /// <summary>
    /// File information result
    /// </summary>
    public class FileInfoResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? FilePath { get; set; }
        public long FileSize { get; set; }
        public bool IsSparseImage { get; set; }
        public SparseFileInfo? SparseInfo { get; set; }
    }

    /// <summary>
    /// Detailed Sparse file information
    /// </summary>
    public class SparseFileInfo
    {
        public string Version { get; set; } = "";
        public uint BlockSize { get; set; }
        public uint TotalBlocks { get; set; }
        public uint TotalChunks { get; set; }
        public long UncompressedSize { get; set; }
    }

    /// <summary>
    /// Compares size and type of two files
    /// </summary>
    public static FileComparisonResult CompareFiles(string file1, string file2)
    {
        if (!File.Exists(file1))
        {
            throw new FileNotFoundException($"File not found: {file1}");
        }

        if (!File.Exists(file2))
        {
            throw new FileNotFoundException($"File not found: {file2}");
        }

        var info1 = new FileInfo(file1);
        var info2 = new FileInfo(file2);

        var result = new FileComparisonResult
        {
            Success = true,
            File1Info = new FileBasicInfo
            {
                Path = file1,
                Size = info1.Length,
                Type = SparseImageValidator.IsSparseImage(file1) ? "Sparse" : "Raw"
            },
            File2Info = new FileBasicInfo
            {
                Path = file2,
                Size = info2.Length,
                Type = SparseImageValidator.IsSparseImage(file2) ? "Sparse" : "Raw"
            }
        };

        if (info1.Length != info2.Length)
        {
            var ratio = (double)Math.Min(info1.Length, info2.Length) / Math.Max(info1.Length, info2.Length) * 100;
            result.CompressionRatio = 100 - ratio;
        }

        return result;
    }

    /// <summary>
    /// File comparison result
    /// </summary>
    public class FileComparisonResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public FileBasicInfo? File1Info { get; set; }
        public FileBasicInfo? File2Info { get; set; }
        public double? CompressionRatio { get; set; }
    }

    /// <summary>
    /// Basic file information
    /// </summary>
    public class FileBasicInfo
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public string Type { get; set; } = "";
    }

    /// <summary>
    /// Verifies the consistency of the conversion result
    /// </summary>
    public static ConversionVerificationResult VerifyConversion(string originalFile, string convertedFile)
    {
        try
        {
            var original = new FileInfo(originalFile);
            var converted = new FileInfo(convertedFile);

            return new ConversionVerificationResult
            {
                Success = true,
                OriginalSize = original.Length,
                ConvertedSize = converted.Length,
                SizesMatch = original.Length == converted.Length
            };
        }
        catch (Exception ex)
        {
            return new ConversionVerificationResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Conversion verification result
    /// </summary>
    public class ConversionVerificationResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public long OriginalSize { get; set; }
        public long ConvertedSize { get; set; }
        public bool SizesMatch { get; set; }
    }

    /// <summary>
    /// Creates a test sparse image
    /// </summary>
    public static TestImageCreationResult CreateTestSparseImage(string outputPath, uint sizeInMB = 100, uint blockSize = 4096)
    {
        try
        {
            var totalSize = (long)sizeInMB * 1024 * 1024;
            var sparseFile = new SparseFile(blockSize, totalSize);
            var testData = Enumerable.Range(0, (int)blockSize).Select(i => (byte)(i % 256)).ToArray();
            for (uint i = 0; i < 10; i++)
            {
                sparseFile.AddRawChunk(testData);
            }
            sparseFile.AddFillChunk(0xDEADBEEF, blockSize * 50);
            sparseFile.AddDontCareChunk(blockSize * 100);

            using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            sparseFile.WriteToStream(outputStream);

            return new TestImageCreationResult
            {
                Success = true,
                OutputPath = outputPath,
                SizeInMB = sizeInMB,
                BlockSize = blockSize,
                TotalChunks = sparseFile.Chunks.Count
            };
        }
        catch (Exception ex)
        {
            return new TestImageCreationResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// Test image creation result
    /// </summary>
    public class TestImageCreationResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? OutputPath { get; set; }
        public uint SizeInMB { get; set; }
        public uint BlockSize { get; set; }
        public int TotalChunks { get; set; }
    }

    /// <summary>
    /// Extracts valid data from a sparse image
    /// </summary>
    public static DataExtractionResult ExtractValidData(string inputPath, string outputPath, long partitionOffset)
    {
        try
        {
            if (!SparseImageValidator.IsSparseImage(inputPath))
            {
                return new DataExtractionResult
                {
                    Success = false,
                    ErrorMessage = "Input file is not a valid sparse image"
                };
            }

            using var inputStream = new FileStream(inputPath, FileMode.Open, FileAccess.Read);
            using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

            var sparseFile = SparseFile.FromStream(inputStream);
            var blockSize = sparseFile.Header.BlockSize;
            var startBlock = (uint)(partitionOffset / blockSize);
            var offsetInBlock = partitionOffset % blockSize;

            var currentBlock = 0u;
            var dataExtracted = false;
            long totalBytesExtracted = 0;

            foreach (var chunk in sparseFile.Chunks)
            {
                var chunkEndBlock = currentBlock + chunk.Header.ChunkSize;
                if (chunkEndBlock > startBlock)
                {
                    switch (chunk.Header.ChunkType)
                    {
                        case SparseFormat.ChunkTypeRaw:
                            if (chunk.DataProvider != null)
                            {
                                var bytesExtracted = ExtractRawChunkData(chunk, currentBlock, startBlock, offsetInBlock, blockSize, outputStream);
                                totalBytesExtracted += bytesExtracted;
                                if (bytesExtracted > 0)
                                {
                                    dataExtracted = true;
                                }
                            }
                            break;

                        case SparseFormat.ChunkTypeFill:
                            var fillBytesExtracted = ExtractFillChunkData(chunk, currentBlock, startBlock, offsetInBlock, blockSize, outputStream);
                            totalBytesExtracted += fillBytesExtracted;
                            if (fillBytesExtracted > 0)
                            {
                                dataExtracted = true;
                            }
                            break;

                        case SparseFormat.ChunkTypeDontCare:
                            break;

                        case SparseFormat.ChunkTypeCrc32:
                            break;

                        default:
                            break;
                    }
                }

                currentBlock = chunkEndBlock;
            }

            return new DataExtractionResult
            {
                Success = true,
                InputPath = inputPath,
                OutputPath = outputPath,
                PartitionOffset = partitionOffset,
                BlockSize = blockSize,
                StartBlock = startBlock,
                OffsetInBlock = offsetInBlock,
                TotalBytesExtracted = totalBytesExtracted,
                DataFound = dataExtracted
            };
        }
        catch (Exception ex)
        {
            return new DataExtractionResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// 数据提取结果
    /// </summary>
    public class DataExtractionResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? InputPath { get; set; }
        public string? OutputPath { get; set; }
        public long PartitionOffset { get; set; }
        public uint BlockSize { get; set; }
        public uint StartBlock { get; set; }
        public long OffsetInBlock { get; set; }
        public long TotalBytesExtracted { get; set; }
        public bool DataFound { get; set; }
    }

    /// <summary>
    /// <summary>
    /// 从原始数据chunk中提取数据
    /// </summary>
    private static long ExtractRawChunkData(SparseChunk chunk, uint currentBlock, uint startBlock, long offsetInBlock, uint blockSize, Stream outputStream)
    {
        if (chunk.DataProvider == null)
        {
            return 0;
        }

        long startOffsetInChunk = 0;
        if (currentBlock <= startBlock && startBlock < currentBlock + chunk.Header.ChunkSize)
        {
            var blocksToSkip = startBlock - currentBlock;
            startOffsetInChunk = (blocksToSkip * blockSize) + offsetInBlock;
        }
        else if (currentBlock < startBlock)
        {
            return 0;
        }

        var length = chunk.DataProvider.Length - startOffsetInChunk;
        if (length <= 0)
        {
            return 0;
        }

        var buffer = new byte[1024 * 1024];
        long totalRead = 0;
        while (totalRead < length)
        {
            var toRead = (int)Math.Min(buffer.Length, length - totalRead);
            var read = chunk.DataProvider.Read(startOffsetInChunk + totalRead, buffer, 0, toRead);
            if (read <= 0)
            {
                break;
            }

            outputStream.Write(buffer, 0, read);
            totalRead += read;
        }
        return totalRead;
    }

    /// <summary>
    /// 从填充chunk中提取数据
    /// </summary>
    private static long ExtractFillChunkData(SparseChunk chunk, uint currentBlock, uint startBlock, long offsetInBlock, uint blockSize, Stream outputStream)
    {
        var fillBytes = BitConverter.GetBytes(chunk.FillValue);
        var totalSize = (long)chunk.Header.ChunkSize * blockSize;

        if (currentBlock <= startBlock && startBlock < currentBlock + chunk.Header.ChunkSize)
        {
            var blocksToSkip = startBlock - currentBlock;
            var bytesToSkip = (blocksToSkip * blockSize) + offsetInBlock;

            if (bytesToSkip < totalSize)
            {
                var remainingBytes = totalSize - bytesToSkip;
                WriteFillData(outputStream, fillBytes, remainingBytes);
                return remainingBytes;
            }
        }
        else if (currentBlock >= startBlock)
        {
            WriteFillData(outputStream, fillBytes, totalSize);
            return totalSize;
        }

        return 0;
    }

    /// <summary>
    /// 提取有效数据并生成CSV对应表
    /// CSV格式：[序号],文件中偏移量(b),文件中长度(b),设备中偏移量(b),设备中长度(b)
    /// 贼要求的，在工具箱里面应该用不到这个功能吧
    /// </summary>
    public static DataExtractionWithCsvResult ExtractValidDataWithCsv(string sparseImagePath, string binOutputPath, string csvOutputPath, long partitionOffset)
    {
        try
        {
            if (!SparseImageValidator.IsSparseImage(sparseImagePath))
            {
                return new DataExtractionWithCsvResult
                {
                    Success = false,
                    ErrorMessage = "不是有效的 sparse 镜像文件"
                };
            }

            using var stream = new FileStream(sparseImagePath, FileMode.Open, FileAccess.Read);
            var sparseFile = SparseFile.FromStream(stream);
            var header = sparseFile.Header;

            var blockSize = header.BlockSize;
            var startBlockNumber = partitionOffset / blockSize;
            var blockOffset = partitionOffset % blockSize;

            var csvRecords = new List<string>
            {
                "序号,文件中偏移量(b),文件中长度(b),设备中偏移量(b),设备中长度(b)"
            };

            using var outputStream = new FileStream(binOutputPath, FileMode.Create, FileAccess.Write);

            var currentBlockNumber = 0u;
            var sequenceNumber = 1;
            var fileOffset = 0L;
            var foundValidData = false;

            foreach (var chunk in sparseFile.Chunks)
            {
                var chunkStartBlock = currentBlockNumber;
                var chunkEndBlock = currentBlockNumber + chunk.Header.ChunkSize;
                if (chunkEndBlock > startBlockNumber)
                {
                    switch (chunk.Header.ChunkType)
                    {
                        case SparseFormat.ChunkTypeRaw:
                            var skipBytes = 0L;
                            var dataLength = (long)(chunk.Header.ChunkSize * blockSize);
                            if (startBlockNumber >= chunkStartBlock && startBlockNumber < chunkEndBlock)
                            {
                                skipBytes = ((startBlockNumber - chunkStartBlock) * blockSize) + blockOffset;
                                dataLength -= skipBytes;
                            }
                            else if (startBlockNumber < chunkStartBlock)
                            {
                                skipBytes = 0;
                            }

                            if (dataLength > 0 && chunk.DataProvider != null)
                            {
                                var chunkFileOffset = fileOffset;
                                var sourceOffset = skipBytes;
                                var lengthToCopy = Math.Min(dataLength, chunk.DataProvider.Length - sourceOffset);

                                if (lengthToCopy > 0)
                                {
                                    var buffer = new byte[1024 * 1024];
                                    long chunkRead = 0;
                                    while (chunkRead < lengthToCopy)
                                    {
                                        var toRead = (int)Math.Min(buffer.Length, lengthToCopy - chunkRead);
                                        var read = chunk.DataProvider.Read(sourceOffset + chunkRead, buffer, 0, toRead);
                                        if (read <= 0)
                                        {
                                            break;
                                        }

                                        outputStream.Write(buffer, 0, read);
                                        chunkRead += read;
                                    }
                                    fileOffset += lengthToCopy;
                                    var deviceOffset = Math.Max(partitionOffset, (long)chunkStartBlock * blockSize);
                                    csvRecords.Add($"{sequenceNumber},{chunkFileOffset},{lengthToCopy},{deviceOffset},{lengthToCopy}");
                                    sequenceNumber++;
                                    foundValidData = true;
                                }
                            }
                            break;

                        case SparseFormat.ChunkTypeFill:
                            var fillBytes = BitConverter.GetBytes(chunk.FillValue);
                            var fillDataLength = (long)(chunk.Header.ChunkSize * blockSize);
                            var fillSkipBytes = 0L;
                            if (startBlockNumber >= chunkStartBlock && startBlockNumber < chunkEndBlock)
                            {
                                fillSkipBytes = ((startBlockNumber - chunkStartBlock) * blockSize) + blockOffset;
                                fillDataLength -= fillSkipBytes;
                            }
                            else if (startBlockNumber < chunkStartBlock)
                            {
                                fillSkipBytes = 0;
                            }

                            if (fillDataLength > 0)
                            {
                                var fillFileOffset = fileOffset;
                                WriteFillData(outputStream, fillBytes, fillDataLength);
                                fileOffset += fillDataLength;
                                var fillDeviceOffset = Math.Max(partitionOffset, chunkStartBlock * blockSize);
                                csvRecords.Add($"{sequenceNumber},{fillFileOffset},{fillDataLength},{fillDeviceOffset},{fillDataLength}");
                                sequenceNumber++;
                                foundValidData = true;
                            }
                            break;

                        case SparseFormat.ChunkTypeDontCare:
                            break;

                        case SparseFormat.ChunkTypeCrc32:
                            break;

                        default:
                            break;
                    }
                }

                currentBlockNumber += chunk.Header.ChunkSize;
            }
            File.WriteAllLines(csvOutputPath, csvRecords);

            return new DataExtractionWithCsvResult
            {
                Success = true,
                InputPath = sparseImagePath,
                BinOutputPath = binOutputPath,
                CsvOutputPath = csvOutputPath,
                PartitionOffset = partitionOffset,
                BlockSize = blockSize,
                StartBlockNumber = startBlockNumber,
                BlockOffset = blockOffset,
                TotalBytesExtracted = fileOffset,
                CsvRecordCount = csvRecords.Count - 1,
                DataFound = foundValidData
            };
        }
        catch (Exception ex)
        {
            return new DataExtractionWithCsvResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <summary>
    /// 提取有效数据并生成CSV的结果
    /// </summary>
    public class DataExtractionWithCsvResult
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public string? InputPath { get; set; }
        public string? BinOutputPath { get; set; }
        public string? CsvOutputPath { get; set; }
        public long PartitionOffset { get; set; }
        public uint BlockSize { get; set; }
        public long StartBlockNumber { get; set; }
        public long BlockOffset { get; set; }
        public long TotalBytesExtracted { get; set; }
        public int CsvRecordCount { get; set; }
        public bool DataFound { get; set; }
    }

    /// <summary>
    /// 写入填充数据
    /// </summary>
    private static void WriteFillData(Stream outputStream, byte[] fillPattern, long totalBytes)
    {
        var remainingBytes = totalBytes;

        while (remainingBytes > 0)
        {
            var bytesToWrite = (int)Math.Min(remainingBytes, fillPattern.Length);
            outputStream.Write(fillPattern, 0, bytesToWrite);
            remainingBytes -= bytesToWrite;
        }
    }
}
