using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace LibSparseSharp;

public class SparseFile : IDisposable
{
    public static SparseHeader PeekHeader(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        Span<byte> headerData = stackalloc byte[SparseFormat.SparseHeaderSize];
        stream.ReadExactly(headerData);
        return SparseHeader.FromBytes(headerData);
    }

    public SparseHeader Header { get; set; }
    public List<SparseChunk> Chunks { get; set; } = [];
    public bool Verbose { get; set; } = false;

    /// <summary>
    /// Gets the total number of blocks already added (the actual maximum coverage range)
    /// </summary>
    public uint CurrentBlock
    {
        get
        {
            if (Chunks.Count == 0)
            {
                return 0;
            }
            var last = Chunks.MaxBy(c => c.StartBlock);
            return last!.StartBlock + last.Header.ChunkSize;
        }
    }

    public SparseFile() { }

    public SparseFile(uint blockSize, long totalSize, bool verbose = false)
    {
        Verbose = verbose;
        var totalBlocks = (uint)((totalSize + blockSize - 1) / blockSize);
        Header = new SparseHeader
        {
            Magic = SparseFormat.SparseHeaderMagic,
            MajorVersion = 1,
            MinorVersion = 0,
            FileHeaderSize = SparseFormat.SparseHeaderSize,
            ChunkHeaderSize = SparseFormat.ChunkHeaderSize,
            BlockSize = blockSize,
            TotalBlocks = totalBlocks,
            TotalChunks = 0,
            ImageChecksum = 0
        };
    }

    public static SparseFile FromStream(Stream stream, bool validateCrc = false, bool verbose = false) => FromStreamInternal(stream, null, validateCrc, verbose);

    public static SparseFile FromBuffer(byte[] buffer, bool validateCrc = false, bool verbose = false)
    {
        using var ms = new MemoryStream(buffer);
        return FromStream(ms, validateCrc, verbose);
    }

    public static SparseFile FromImageFile(string filePath, bool validateCrc = false, bool verbose = false)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return FromStreamInternal(stream, filePath, validateCrc, verbose);
    }

    private static SparseFile FromStreamInternal(Stream stream, string? filePath, bool validateCrc, bool verbose = false)
    {
        var sparseFile = new SparseFile { Verbose = verbose };
        Span<byte> headerData = stackalloc byte[SparseFormat.SparseHeaderSize];
        stream.ReadExactly(headerData);

        sparseFile.Header = SparseHeader.FromBytes(headerData);

        if (verbose)
        {
            SparseLogger.Info($"Parsing Sparse image header: BlockSize={sparseFile.Header.BlockSize}, TotalBlocks={sparseFile.Header.TotalBlocks}, TotalChunks={sparseFile.Header.TotalChunks}");
        }

        if (!sparseFile.Header.IsValid())
        {
            throw new InvalidDataException("Invalid sparse header");
        }

        if (sparseFile.Header.FileHeaderSize > SparseFormat.SparseHeaderSize)
        {
            stream.Seek(sparseFile.Header.FileHeaderSize - SparseFormat.SparseHeaderSize, SeekOrigin.Current);
        }

        var checksum = Crc32.Begin();
        var buffer = new byte[1024 * 1024];
        uint currentBlock = 0;

        Span<byte> chunkHeaderData = stackalloc byte[SparseFormat.ChunkHeaderSize];
        Span<byte> fillData = stackalloc byte[4];
        for (uint i = 0; i < sparseFile.Header.TotalChunks; i++)
        {
            stream.ReadExactly(chunkHeaderData);

            var chunkHeader = ChunkHeader.FromBytes(chunkHeaderData);

            if (verbose)
            {
                SparseLogger.Info($"Chunk #{i}: Type=0x{chunkHeader.ChunkType:X4}, Size={chunkHeader.ChunkSize} blocks, Total Size={chunkHeader.TotalSize}");
            }

            if (sparseFile.Header.ChunkHeaderSize > SparseFormat.ChunkHeaderSize)
            {
                stream.Seek(sparseFile.Header.ChunkHeaderSize - SparseFormat.ChunkHeaderSize, SeekOrigin.Current);
            }

            var chunk = new SparseChunk(chunkHeader) { StartBlock = currentBlock };

            if (!chunkHeader.IsValid())
            {
                throw new InvalidDataException($"Invalid chunk header for chunk {i}: Type 0x{chunkHeader.ChunkType:X4}");
            }

            var dataSize = (long)chunkHeader.TotalSize - sparseFile.Header.ChunkHeaderSize;
            var expectedRawSize = (long)chunkHeader.ChunkSize * sparseFile.Header.BlockSize;

            switch (chunkHeader.ChunkType)
            {
                case SparseFormat.ChunkTypeRaw:
                    if (dataSize != expectedRawSize)
                    {
                        throw new InvalidDataException($"Total size ({chunkHeader.TotalSize}) for RAW chunk {i} does not match expected data size ({expectedRawSize})");
                    }

                    if (validateCrc)
                    {
                        var remaining = dataSize;
                        while (remaining > 0)
                        {
                            var toRead = (int)Math.Min(buffer.Length, remaining);
                            stream.ReadExactly(buffer, 0, toRead);
                            checksum = Crc32.Update(checksum, buffer, 0, toRead);
                            remaining -= toRead;
                        }

                        if (filePath != null)
                        {
                            chunk.DataProvider = new FileDataProvider(filePath, stream.Position - dataSize, dataSize);
                        }
                        else
                        {
                            stream.Seek(-dataSize, SeekOrigin.Current);
                            var rawData = new byte[dataSize];
                            stream.ReadExactly(rawData);
                            chunk.DataProvider = new MemoryDataProvider(rawData);
                        }
                    }
                    else if (filePath != null)
                    {
                        chunk.DataProvider = new FileDataProvider(filePath, stream.Position, dataSize);
                        stream.Seek(dataSize, SeekOrigin.Current);
                    }
                    else
                    {
                        if (dataSize > int.MaxValue)
                        {
                            throw new NotSupportedException($"Raw data for chunk {i} is too large ({dataSize} bytes), exceeding memory buffer limits.");
                        }
                        var rawData = new byte[dataSize];
                        stream.ReadExactly(rawData);
                        chunk.DataProvider = new MemoryDataProvider(rawData);
                    }
                    break;

                case SparseFormat.ChunkTypeFill:
                    if (dataSize < 4)
                    {
                        throw new InvalidDataException($"Data size ({dataSize}) for FILL chunk {i} is less than 4 bytes");
                    }

                    stream.ReadExactly(fillData);

                    chunk.FillValue = BinaryPrimitives.ReadUInt32LittleEndian(fillData);

                    if (validateCrc)
                    {
                        var valBytes = new byte[4];
                        BinaryPrimitives.WriteUInt32LittleEndian(valBytes, chunk.FillValue);
                        for (var j = 0; j <= buffer.Length - 4; j += 4)
                        {
                            Array.Copy(valBytes, 0, buffer, j, 4);
                        }

                        var remainingFilling = expectedRawSize;
                        while (remainingFilling > 0)
                        {
                            var toProcess = (int)Math.Min(buffer.Length, remainingFilling);
                            checksum = Crc32.Update(checksum, buffer, 0, toProcess);
                            remainingFilling -= toProcess;
                        }
                    }

                    if (dataSize > 4)
                    {
                        stream.Seek(dataSize - 4, SeekOrigin.Current);
                    }
                    break;

                case SparseFormat.ChunkTypeDontCare:
                    if (validateCrc)
                    {
                        Array.Clear(buffer, 0, buffer.Length);
                        var remainingDontCare = expectedRawSize;
                        while (remainingDontCare > 0)
                        {
                            var toProcess = (int)Math.Min(buffer.Length, remainingDontCare);
                            checksum = Crc32.Update(checksum, buffer, 0, toProcess);
                            remainingDontCare -= toProcess;
                        }
                    }
                    if (dataSize > 0)
                    {
                        stream.Seek(dataSize, SeekOrigin.Current);
                    }
                    break;

                case SparseFormat.ChunkTypeCrc32:
                    if (dataSize >= 4)
                    {
                        var crcFileData = new byte[4];
                        if (stream.Read(crcFileData, 0, 4) != 4)
                        {
                            throw new InvalidDataException($"Failed to read CRC32 value for chunk {i}");
                        }
                        var fileCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcFileData);
                        if (validateCrc && fileCrc != Crc32.Finish(checksum))
                        {
                            throw new InvalidDataException($"CRC32 checksum mismatch: file has 0x{fileCrc:X8}, computed 0x{Crc32.Finish(checksum):X8}");
                        }
                        if (dataSize > 4)
                        {
                            stream.Seek(dataSize - 4, SeekOrigin.Current);
                        }
                    }
                    break;

                default:
                    throw new InvalidDataException($"Unknown chunk type for chunk {i}: 0x{chunkHeader.ChunkType:X4}");
            }

            if (chunkHeader.ChunkType != SparseFormat.ChunkTypeCrc32)
            {
                sparseFile.Chunks.Add(chunk);
                currentBlock += chunkHeader.ChunkSize;
            }
        }

        if (verbose)
        {
            SparseLogger.Info($"Sparse image parsing completed: {sparseFile.Chunks.Count} chunks, {currentBlock} blocks total");
        }

        return sparseFile.Header.TotalBlocks != currentBlock
            ? throw new InvalidDataException($"Block count mismatch: Sparse header expects {sparseFile.Header.TotalBlocks} blocks, but parsed {currentBlock}")
            : sparseFile;
    }

    /// <summary>
    /// Writes the sparse file to stream (aligned with libsparse's sparse_file_write)
    /// </summary>
    public void WriteToStream(Stream stream, bool sparse = true, bool gzip = false, bool includeCrc = false)
    {
        if (!sparse)
        {
            WriteRawToStream(stream);
            return;
        }

        var targetStream = stream;
        if (gzip)
        {
            targetStream = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Compress, true);
        }

        try
        {
            // Pre-processing: merge/fill gaps
            var sortedChunks = Chunks.OrderBy(c => c.StartBlock).ToList();
            var finalChunks = new List<SparseChunk>();
            uint currentBlock = 0;

            foreach (var chunk in sortedChunks)
            {
                if (chunk.StartBlock > currentBlock)
                {
                    // Fill gaps
                    var gapBlocks = chunk.StartBlock - currentBlock;
                    finalChunks.Add(new SparseChunk(new ChunkHeader
                    {
                        ChunkType = SparseFormat.ChunkTypeDontCare,
                        ChunkSize = gapBlocks,
                        TotalSize = SparseFormat.ChunkHeaderSize
                    })
                    { StartBlock = currentBlock });
                }
                finalChunks.Add(chunk);
                currentBlock = chunk.StartBlock + chunk.Header.ChunkSize;
            }

            var outHeader = Header;
            var sumBlocks = currentBlock;

            var needsTrailingSkip = outHeader.TotalBlocks > sumBlocks;

            var totalChunks = (uint)finalChunks.Count;
            if (needsTrailingSkip)
            {
                totalChunks++;
            }
            if (includeCrc)
            {
                totalChunks++;
            }

            outHeader = Header with { TotalChunks = totalChunks };
            if (sumBlocks > outHeader.TotalBlocks)
            {
                outHeader = outHeader with { TotalBlocks = sumBlocks };
            }

            Span<byte> headerData = stackalloc byte[SparseFormat.SparseHeaderSize];
            outHeader.WriteTo(headerData);
            targetStream.Write(headerData);

            var checksum = Crc32.Begin();
            var buffer = new byte[1024 * 1024];

            Span<byte> chunkHeaderData = stackalloc byte[SparseFormat.ChunkHeaderSize];
            Span<byte> fillValData = stackalloc byte[4];
            foreach (var chunk in finalChunks)
            {
                chunk.Header.WriteTo(chunkHeaderData);
                targetStream.Write(chunkHeaderData);

                var expectedDataSize = (long)chunk.Header.ChunkSize * outHeader.BlockSize;

                switch (chunk.Header.ChunkType)
                {
                    case SparseFormat.ChunkTypeRaw:
                        if (chunk.DataProvider != null)
                        {
                            long providerOffset = 0;
                            while (providerOffset < chunk.DataProvider.Length)
                            {
                                var toRead = (int)Math.Min(buffer.Length, chunk.DataProvider.Length - providerOffset);
                                var read = chunk.DataProvider.Read(providerOffset, buffer, 0, toRead);
                                if (read <= 0)
                                {
                                    break;
                                }

                                targetStream.Write(buffer, 0, read);
                                if (includeCrc)
                                {
                                    checksum = Crc32.Update(checksum, buffer, 0, read);
                                }
                                providerOffset += read;
                            }

                            var padding = expectedDataSize - providerOffset;
                            if (padding > 0)
                            {
                                Array.Clear(buffer, 0, (int)Math.Min(buffer.Length, padding));
                                while (padding > 0)
                                {
                                    var toWrite = (int)Math.Min(buffer.Length, padding);
                                    targetStream.Write(buffer, 0, toWrite);
                                    if (includeCrc)
                                    {
                                        checksum = Crc32.Update(checksum, buffer, 0, toWrite);
                                    }
                                    padding -= toWrite;
                                }
                            }
                        }
                        else
                        {
                            Array.Clear(buffer, 0, buffer.Length);
                            var remaining = expectedDataSize;
                            while (remaining > 0)
                            {
                                var toWrite = (int)Math.Min(buffer.Length, remaining);
                                targetStream.Write(buffer, 0, toWrite);
                                if (includeCrc)
                                {
                                    checksum = Crc32.Update(checksum, buffer, 0, toWrite);
                                }
                                remaining -= toWrite;
                            }
                        }
                        break;

                    case SparseFormat.ChunkTypeFill:
                        BinaryPrimitives.WriteUInt32LittleEndian(fillValData, chunk.FillValue);
                        targetStream.Write(fillValData);
                        if (includeCrc)
                        {
                            for (var i = 0; i <= buffer.Length - 4; i += 4)
                            {
                                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(i), chunk.FillValue);
                            }

                            long processed = 0;
                            while (processed < expectedDataSize)
                            {
                                var toProcess = (int)Math.Min(buffer.Length, expectedDataSize - processed);
                                checksum = Crc32.Update(checksum, buffer, 0, toProcess);
                                processed += toProcess;
                            }
                        }
                        break;

                    case SparseFormat.ChunkTypeDontCare:
                        if (includeCrc)
                        {
                            Array.Clear(buffer, 0, buffer.Length);
                            long processed = 0;
                            while (processed < expectedDataSize)
                            {
                                var toProcess = (int)Math.Min(buffer.Length, expectedDataSize - processed);
                                checksum = Crc32.Update(checksum, buffer, 0, toProcess);
                                processed += toProcess;
                            }
                        }
                        break;

                    case SparseFormat.ChunkTypeCrc32:
                        break;
                    default:
                        break;
                }
            }

            if (needsTrailingSkip)
            {
                var skipSize = outHeader.TotalBlocks - sumBlocks;
                var skipHeader = new ChunkHeader
                {
                    ChunkType = SparseFormat.ChunkTypeDontCare,
                    Reserved = 0,
                    ChunkSize = skipSize,
                    TotalSize = SparseFormat.ChunkHeaderSize
                };
                targetStream.Write(skipHeader.ToBytes(), 0, SparseFormat.ChunkHeaderSize);

                if (includeCrc)
                {
                    var skipDataLen = (long)skipSize * outHeader.BlockSize;
                    Array.Clear(buffer, 0, buffer.Length);
                    long processed = 0;
                    while (processed < skipDataLen)
                    {
                        var toProcess = (int)Math.Min(buffer.Length, skipDataLen - processed);
                        checksum = Crc32.Update(checksum, buffer, 0, toProcess);
                        processed += toProcess;
                    }
                }
            }

            if (includeCrc)
            {
                var finalChecksum = Crc32.Finish(checksum);
                var crcHeader = new ChunkHeader
                {
                    ChunkType = SparseFormat.ChunkTypeCrc32,
                    Reserved = 0,
                    ChunkSize = 0,
                    TotalSize = SparseFormat.ChunkHeaderSize + 4
                };
                var finalCrcData = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(finalCrcData, finalChecksum);
                targetStream.Write(crcHeader.ToBytes(), 0, SparseFormat.ChunkHeaderSize);
                targetStream.Write(finalCrcData, 0, 4);

                outHeader = outHeader with { ImageChecksum = finalChecksum };
                if (targetStream.CanSeek)
                {
                    var currentPos = targetStream.Position;
                    targetStream.Seek(0, SeekOrigin.Begin);
                    targetStream.Write(outHeader.ToBytes(), 0, SparseFormat.SparseHeaderSize);
                    targetStream.Seek(currentPos, SeekOrigin.Begin);
                }
            }
        }
        finally
        {
            if (gzip)
            {
                targetStream.Dispose();
            }
        }
    }

    public void WriteRawToStream(Stream stream, bool sparseMode = false)
    {
        var buffer = new byte[1024 * 1024];
        foreach (var chunk in Chunks)
        {
            var size = (long)chunk.Header.ChunkSize * Header.BlockSize;
            switch (chunk.Header.ChunkType)
            {
                case SparseFormat.ChunkTypeRaw:
                    if (chunk.DataProvider != null)
                    {
                        long written = 0;
                        while (written < chunk.DataProvider.Length)
                        {
                            var toRead = (int)Math.Min(buffer.Length, chunk.DataProvider.Length - written);
                            var read = chunk.DataProvider.Read(written, buffer, 0, toRead);
                            if (read <= 0)
                            {
                                break;
                            }

                            stream.Write(buffer, 0, read);
                            written += read;
                        }
                        if (written < size)
                        {
                            Array.Clear(buffer, 0, (int)Math.Min(buffer.Length, size - written));
                            while (written < size)
                            {
                                var toWrite = (int)Math.Min(buffer.Length, size - written);
                                stream.Write(buffer, 0, toWrite);
                                written += toWrite;
                            }
                        }
                    }
                    else
                    {
                        if (sparseMode && stream.CanSeek)
                        {
                            stream.Seek(size, SeekOrigin.Current);
                        }
                        else
                        {
                            Array.Clear(buffer, 0, buffer.Length);
                            var remaining = size;
                            while (remaining > 0)
                            {
                                var toWrite = (int)Math.Min(buffer.Length, remaining);
                                stream.Write(buffer, 0, toWrite);
                                remaining -= toWrite;
                            }
                        }
                    }
                    break;
                case SparseFormat.ChunkTypeFill:
                    var fillValBytes = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(fillValBytes, chunk.FillValue);
                    for (var i = 0; i <= buffer.Length - 4; i += 4)
                    {
                        Array.Copy(fillValBytes, 0, buffer, i, 4);
                    }

                    var fillRemaining = size;
                    while (fillRemaining > 0)
                    {
                        var toWrite = (int)Math.Min(buffer.Length, fillRemaining);
                        stream.Write(buffer, 0, toWrite);
                        fillRemaining -= toWrite;
                    }
                    break;
                case SparseFormat.ChunkTypeDontCare:
                    if (sparseMode && stream.CanSeek)
                    {
                        stream.Seek(size, SeekOrigin.Current);
                    }
                    else
                    {
                        Array.Clear(buffer, 0, buffer.Length);
                        var remaining = size;
                        while (remaining > 0)
                        {
                            var toWrite = (int)Math.Min(buffer.Length, remaining);
                            stream.Write(buffer, 0, toWrite);
                            remaining -= toWrite;
                        }
                    }
                    break;
                default:
                    break;
            }
        }

        var expectedFullLength = (long)Header.TotalBlocks * Header.BlockSize;
        if (stream.CanSeek && stream.Length < expectedFullLength)
        {
            stream.SetLength(expectedFullLength);
        }
    }

    /// <summary>
    /// Gets the specified block start index and checks for overlaps
    /// </summary>
    private uint GetNextBlockAndCheckOverlap(uint? blockIndex, uint sizeInBlocks)
    {
        var start = blockIndex ?? CurrentBlock;
        foreach (var chunk in Chunks)
        {
            if (start < chunk.StartBlock + chunk.Header.ChunkSize && start + sizeInBlocks > chunk.StartBlock)
            {
                throw new ArgumentException($"Block region [{start}, {start + sizeInBlocks}) overlaps with existing chunk [{chunk.StartBlock}, {chunk.StartBlock + chunk.Header.ChunkSize}).");
            }
        }
        return start;
    }

    private void AddChunkSorted(SparseChunk chunk)
    {
        var index = Chunks.FindIndex(c => c.StartBlock > chunk.StartBlock);
        if (index == -1)
        {
            Chunks.Add(chunk);
        }
        else
        {
            Chunks.Insert(index, chunk);
        }
    }

    public delegate int SparseWriteCallback(byte[]? data, int length);

    /// <summary>
    /// Writes the sparse file using a callback function (aligns with libsparse's sparse_file_callback)
    /// </summary>
    public void WriteWithCallback(SparseWriteCallback callback, bool sparse = true, bool includeCrc = false)
    {
        if (!sparse)
        {
            var buffer = new byte[1024 * 1024];
            foreach (var chunk in Chunks)
            {
                var size = (long)chunk.Header.ChunkSize * Header.BlockSize;
                switch (chunk.Header.ChunkType)
                {
                    case SparseFormat.ChunkTypeRaw:
                        if (chunk.DataProvider != null)
                        {
                            long written = 0;
                            while (written < chunk.DataProvider.Length)
                            {
                                var toRead = (int)Math.Min(buffer.Length, chunk.DataProvider.Length - written);
                                var read = chunk.DataProvider.Read(written, buffer, 0, toRead);
                                if (read <= 0)
                                {
                                    break;
                                }

                                if (callback(buffer, read) < 0)
                                {
                                    return;
                                }

                                written += read;
                            }
                            if (written < size)
                            {
                                Array.Clear(buffer, 0, (int)Math.Min(buffer.Length, size - written));
                                while (written < size)
                                {
                                    var toWrite = (int)Math.Min(buffer.Length, size - written);
                                    if (callback(buffer, toWrite) < 0)
                                    {
                                        return;
                                    }

                                    written += toWrite;
                                }
                            }
                        }
                        else
                        {
                            if (callback(null, (int)size) < 0)
                            {
                                return;
                            }
                        }
                        break;
                    case SparseFormat.ChunkTypeFill:
                        var fillValBytes = new byte[4];
                        BinaryPrimitives.WriteUInt32LittleEndian(fillValBytes, chunk.FillValue);
                        for (var i = 0; i <= buffer.Length - 4; i += 4)
                        {
                            Array.Copy(fillValBytes, 0, buffer, i, 4);
                        }

                        var fillRemaining = size;
                        while (fillRemaining > 0)
                        {
                            var toWrite = (int)Math.Min(buffer.Length, fillRemaining);
                            if (callback(buffer, toWrite) < 0)
                            {
                                return;
                            }

                            fillRemaining -= toWrite;
                        }
                        break;
                    case SparseFormat.ChunkTypeDontCare:
                        if (callback(null, (int)size) < 0)
                        {
                            return;
                        }

                        break;
                    default:
                        break;
                }
            }
            return;
        }

        // Callback writing in Sparse mode
        using var ms = new MemoryStream();
        WriteToStream(ms, true, false, includeCrc);
        var bytes = ms.ToArray();
        callback(bytes, bytes.Length);
    }

    public void AddRawFileChunk(string filePath, long offset, uint size, uint? blockIndex = null)
    {
        var blockSize = Header.BlockSize;
        var totalBlocks = (size + blockSize - 1) / blockSize;
        var currentBlockStart = GetNextBlockAndCheckOverlap(blockIndex, totalBlocks);

        var remaining = size;
        var currentOffset = offset;

        while (remaining > 0)
        {
            var partSize = Math.Min(remaining, SparseFormat.MaxChunkDataSize);
            if (partSize < remaining && partSize % blockSize != 0)
            {
                partSize = partSize / blockSize * blockSize;
                if (partSize == 0)
                {
                    partSize = remaining;
                }
            }

            var chunkBlocks = (partSize + blockSize - 1) / blockSize;
            var chunkHeader = new ChunkHeader
            {
                ChunkType = SparseFormat.ChunkTypeRaw,
                Reserved = 0,
                ChunkSize = chunkBlocks,
                TotalSize = (uint)(SparseFormat.ChunkHeaderSize + ((long)chunkBlocks * blockSize))
            };

            var chunk = new SparseChunk(chunkHeader)
            {
                StartBlock = currentBlockStart,
                DataProvider = new FileDataProvider(filePath, currentOffset, partSize)
            };

            AddChunkSorted(chunk);
            currentBlockStart += chunkBlocks;
            remaining -= partSize;
            currentOffset += partSize;
        }
    }

    public void AddRawChunk(byte[] data, uint? blockIndex = null)
    {
        var blockSize = Header.BlockSize;
        var totalBlocks = (uint)((data.Length + blockSize - 1) / blockSize);
        var currentBlockStart = GetNextBlockAndCheckOverlap(blockIndex, totalBlocks);

        var remaining = (uint)data.Length;
        var currentOffset = 0;

        while (remaining > 0)
        {
            var partSize = Math.Min(remaining, SparseFormat.MaxChunkDataSize);
            if (partSize < remaining && partSize % blockSize != 0)
            {
                partSize = partSize / blockSize * blockSize;
                if (partSize == 0)
                {
                    partSize = remaining;
                }
            }

            var chunkBlocks = (partSize + blockSize - 1) / blockSize;
            var chunkHeader = new ChunkHeader
            {
                ChunkType = SparseFormat.ChunkTypeRaw,
                Reserved = 0,
                ChunkSize = chunkBlocks,
                TotalSize = (uint)(SparseFormat.ChunkHeaderSize + ((long)chunkBlocks * blockSize))
            };

            var chunk = new SparseChunk(chunkHeader)
            {
                StartBlock = currentBlockStart,
                DataProvider = new MemoryDataProvider(data, currentOffset, (int)partSize)
            };

            AddChunkSorted(chunk);
            currentBlockStart += chunkBlocks;
            remaining -= partSize;
            currentOffset += (int)partSize;
        }
    }

    public void AddStreamChunk(Stream stream, long offset, uint size, uint? blockIndex = null, bool leaveOpen = true)
    {
        var blockSize = Header.BlockSize;
        var totalBlocks = (size + blockSize - 1) / blockSize;
        var currentBlockStart = GetNextBlockAndCheckOverlap(blockIndex, totalBlocks);

        var remaining = size;
        var currentOffset = offset;

        while (remaining > 0)
        {
            var partSize = Math.Min(remaining, SparseFormat.MaxChunkDataSize);
            if (partSize < remaining && partSize % blockSize != 0)
            {
                partSize = partSize / blockSize * blockSize;
                if (partSize == 0)
                {
                    partSize = remaining;
                }
            }

            var chunkBlocks = (partSize + blockSize - 1) / blockSize;
            var chunkHeader = new ChunkHeader
            {
                ChunkType = SparseFormat.ChunkTypeRaw,
                Reserved = 0,
                ChunkSize = chunkBlocks,
                TotalSize = (uint)(SparseFormat.ChunkHeaderSize + ((long)chunkBlocks * blockSize))
            };

            var chunk = new SparseChunk(chunkHeader)
            {
                StartBlock = currentBlockStart,
                DataProvider = new StreamDataProvider(stream, currentOffset, partSize, leaveOpen)
            };

            AddChunkSorted(chunk);
            currentBlockStart += chunkBlocks;
            remaining -= partSize;
            currentOffset += partSize;
        }
    }

    public void AddFillChunk(uint fillValue, long size, uint? blockIndex = null)
    {
        var blockSize = Header.BlockSize;
        var totalBlocks = (uint)((size + blockSize - 1) / blockSize);
        var currentBlockStart = GetNextBlockAndCheckOverlap(blockIndex, totalBlocks);

        var remaining = size;

        while (remaining > 0)
        {
            var partSize = Math.Min(remaining, (long)0x00FFFFFF * blockSize);

            var partBlocks = (uint)((partSize + blockSize - 1) / blockSize);
            if (partBlocks > 0x00FFFFFF)
            {
                partBlocks = 0x00FFFFFF;
            }

            var actualPartSize = (long)partBlocks * blockSize;
            if (actualPartSize > remaining)
            {
                actualPartSize = remaining;
            }

            var chunkHeader = new ChunkHeader
            {
                ChunkType = SparseFormat.ChunkTypeFill,
                Reserved = 0,
                ChunkSize = partBlocks,
                TotalSize = SparseFormat.ChunkHeaderSize + 4
            };

            var chunk = new SparseChunk(chunkHeader)
            {
                StartBlock = currentBlockStart,
                FillValue = fillValue
            };

            AddChunkSorted(chunk);
            currentBlockStart += partBlocks;
            remaining -= actualPartSize;
        }
    }

    public void AddDontCareChunk(long size, uint? blockIndex = null)
    {
        var blockSize = Header.BlockSize;
        var totalBlocks = (uint)((size + blockSize - 1) / blockSize);
        var currentBlockStart = GetNextBlockAndCheckOverlap(blockIndex, totalBlocks);

        var remaining = size;

        while (remaining > 0)
        {
            var partBlocks = (uint)((remaining + blockSize - 1) / blockSize);
            if (partBlocks > 0x00FFFFFFu)
            {
                partBlocks = 0x00FFFFFFu;
            }

            var actualPartSize = (long)partBlocks * blockSize;

            var chunkHeader = new ChunkHeader
            {
                ChunkType = SparseFormat.ChunkTypeDontCare,
                Reserved = 0,
                ChunkSize = partBlocks,
                TotalSize = SparseFormat.ChunkHeaderSize
            };

            var chunk = new SparseChunk(chunkHeader) { StartBlock = currentBlockStart };
            AddChunkSorted(chunk);
            currentBlockStart += partBlocks;
            remaining -= Math.Min(remaining, actualPartSize);
        }
    }

    public void Dispose()
    {
        foreach (var chunk in Chunks)
        {
            chunk.Dispose();
        }
    }

    public List<SparseFile> Resparse(long maxFileSize)
    {
        var result = new List<SparseFile>();
        long overhead = SparseFormat.SparseHeaderSize + (2 * SparseFormat.ChunkHeaderSize) + 4;

        if (maxFileSize <= overhead)
        {
            throw new ArgumentException($"maxFileSize must be greater than the infrastructure overhead ({overhead} bytes)");
        }

        var fileLimit = maxFileSize - overhead;
        var entries = BuildResparseEntries();

        if (entries.Count == 0)
        {
            var emptyFile = CreateNewSparseForResparse();
            emptyFile.Header = emptyFile.Header with { TotalBlocks = Header.TotalBlocks };
            if (Header.TotalBlocks > 0)
            {
                emptyFile.Chunks.Add(CreateDontCareChunk(Header.TotalBlocks));
            }
            FinishCurrentResparseFile(emptyFile);
            result.Add(emptyFile);
            return result;
        }

        var startIndex = 0;
        while (startIndex < entries.Count)
        {
            long fileLen = 0;
            uint lastBlock = 0;
            var lastIncludedIndex = -1;

            for (var i = startIndex; i < entries.Count; i++)
            {
                var entry = entries[i];
                var count = GetSparseChunkSize(entry.Chunk);
                if (entry.StartBlock > lastBlock)
                {
                    count += SparseFormat.ChunkHeaderSize;
                }

                lastBlock = entry.StartBlock + entry.Chunk.Header.ChunkSize;

                if (fileLen + count > fileLimit)
                {
                    fileLen += SparseFormat.ChunkHeaderSize;
                    var availableForData = fileLimit - fileLen;
                    var canSplit = lastIncludedIndex < 0 || availableForData > (fileLimit / 8);

                    if (canSplit)
                    {
                        var blocksToTake = availableForData > 0
                            ? (uint)(availableForData / Header.BlockSize)
                            : 0u;

                        if (blocksToTake > 0 && blocksToTake < entry.Chunk.Header.ChunkSize)
                        {
                            var (part1, part2) = SplitChunkInternal(entry.Chunk, blocksToTake);
                            entries[i] = new ResparseEntry(entry.StartBlock, part1);
                            entries.Insert(i + 1, new ResparseEntry(entry.StartBlock + blocksToTake, part2));
                            lastIncludedIndex = i;
                        }
                    }

                    break;
                }

                fileLen += count;
                lastIncludedIndex = i;
            }

            if (lastIncludedIndex < startIndex)
            {
                throw new InvalidOperationException("Cannot fit chunk into SparseFile, please increase maxFileSize.");
            }

            var currentFile = BuildResparseFile(entries, startIndex, lastIncludedIndex);
            result.Add(currentFile);
            startIndex = lastIncludedIndex + 1;
        }

        return result;
    }

    private (SparseChunk First, SparseChunk Second) SplitChunkInternal(SparseChunk chunk, uint blocksToTake)
    {
        var h1 = chunk.Header with { ChunkSize = blocksToTake };
        var h2 = chunk.Header with { ChunkSize = chunk.Header.ChunkSize - blocksToTake };

        if (chunk.Header.ChunkType == SparseFormat.ChunkTypeRaw)
        {
            h1 = h1 with { TotalSize = (uint)(SparseFormat.ChunkHeaderSize + ((long)blocksToTake * Header.BlockSize)) };
            h2 = h2 with { TotalSize = (uint)(SparseFormat.ChunkHeaderSize + ((long)h2.ChunkSize * Header.BlockSize)) };
        }
        else if (chunk.Header.ChunkType == SparseFormat.ChunkTypeFill)
        {
            h1 = h1 with { TotalSize = SparseFormat.ChunkHeaderSize + 4 };
            h2 = h2 with { TotalSize = SparseFormat.ChunkHeaderSize + 4 };
        }
        else
        {
            h1 = h1 with { TotalSize = SparseFormat.ChunkHeaderSize };
            h2 = h2 with { TotalSize = SparseFormat.ChunkHeaderSize };
        }

        var part1 = new SparseChunk(h1);
        var part2 = new SparseChunk(h2);

        if (chunk.Header.ChunkType == SparseFormat.ChunkTypeRaw && chunk.DataProvider != null)
        {
            part1.DataProvider = chunk.DataProvider.GetSubProvider(0, (long)blocksToTake * Header.BlockSize);
            part2.DataProvider = chunk.DataProvider.GetSubProvider((long)blocksToTake * Header.BlockSize, (long)h2.ChunkSize * Header.BlockSize);
        }
        else if (chunk.Header.ChunkType == SparseFormat.ChunkTypeFill)
        {
            part1.FillValue = chunk.FillValue;
            part2.FillValue = chunk.FillValue;
        }

        return (part1, part2);
    }

    private SparseFile CreateNewSparseForResparse()
    {
        return new SparseFile
        {
            Header = new SparseHeader
            {
                Magic = SparseFormat.SparseHeaderMagic,
                MajorVersion = Header.MajorVersion,
                MinorVersion = Header.MinorVersion,
                FileHeaderSize = Header.FileHeaderSize,
                ChunkHeaderSize = Header.ChunkHeaderSize,
                BlockSize = Header.BlockSize
            }
        };
    }

    private void FinishCurrentResparseFile(SparseFile file)
    {
        file.Header = file.Header with
        {
            TotalChunks = (uint)file.Chunks.Count,
            TotalBlocks = (uint)file.Chunks.Sum(c => c.Header.ChunkSize)
        };
    }

    private sealed class ResparseEntry(uint startBlock, SparseChunk chunk)
    {
        public uint StartBlock { get; } = startBlock;
        public SparseChunk Chunk { get; } = chunk;
    }

    private List<ResparseEntry> BuildResparseEntries()
    {
        var entries = new List<ResparseEntry>();
        uint currentBlock = 0;

        foreach (var chunk in Chunks)
        {
            switch (chunk.Header.ChunkType)
            {
                case SparseFormat.ChunkTypeRaw:
                case SparseFormat.ChunkTypeFill:
                    entries.Add(new ResparseEntry(currentBlock, chunk));
                    break;
                default:
                    break;
            }

            currentBlock += chunk.Header.ChunkSize;
        }

        return entries;
    }

    private long GetSparseChunkSize(SparseChunk chunk)
    {
        return chunk.Header.ChunkType switch
        {
            SparseFormat.ChunkTypeRaw => SparseFormat.ChunkHeaderSize + ((long)chunk.Header.ChunkSize * Header.BlockSize),
            SparseFormat.ChunkTypeFill => SparseFormat.ChunkHeaderSize + 4,
            _ => SparseFormat.ChunkHeaderSize
        };
    }

    private SparseChunk CreateDontCareChunk(uint blocks)
    {
        return new SparseChunk(new ChunkHeader
        {
            ChunkType = SparseFormat.ChunkTypeDontCare,
            Reserved = 0,
            ChunkSize = blocks,
            TotalSize = SparseFormat.ChunkHeaderSize
        });
    }

    private SparseFile BuildResparseFile(List<ResparseEntry> entries, int startIndex, int endIndex)
    {
        var file = CreateNewSparseForResparse();
        file.Header = file.Header with { TotalBlocks = Header.TotalBlocks };

        uint currentBlock = 0;
        for (var i = startIndex; i <= endIndex; i++)
        {
            var entry = entries[i];
            if (entry.StartBlock > currentBlock)
            {
                file.Chunks.Add(CreateDontCareChunk(entry.StartBlock - currentBlock));
            }

            file.Chunks.Add(entry.Chunk);
            currentBlock = entry.StartBlock + entry.Chunk.Header.ChunkSize;
        }

        if (currentBlock < Header.TotalBlocks)
        {
            file.Chunks.Add(CreateDontCareChunk(Header.TotalBlocks - currentBlock));
        }

        FinishCurrentResparseFile(file);
        return file;
    }

    public Stream GetExportStream(uint startBlock, uint blockCount, bool includeCrc = false) => new SparseImageStream(this, startBlock, blockCount, includeCrc);

    public IEnumerable<Stream> GetResparsedStreams(long maxFileSize, bool includeCrc = false)
    {
        foreach (var file in Resparse(maxFileSize))
        {
            yield return new SparseImageStream(file, 0, file.Header.TotalBlocks, includeCrc, false);
        }
    }

    /// <summary>
    /// Traverses all chunks containing data (RAW/FILL) (aligns with libsparse's sparse_file_foreach_chunk)
    /// </summary>
    public void ForeachChunk(Action<SparseChunk, uint, uint> action)
    {
        uint currentBlock = 0;
        foreach (var chunk in Chunks)
        {
            if (chunk.Header.ChunkType is SparseFormat.ChunkTypeRaw or
                SparseFormat.ChunkTypeFill)
            {
                action(chunk, currentBlock, chunk.Header.ChunkSize);
            }
            currentBlock += chunk.Header.ChunkSize;
        }
    }

    /// <summary>
    /// Traverses all chunks, including DONT_CARE
    /// </summary>
    public void ForeachChunkAll(Action<SparseChunk, uint, uint> action)
    {
        uint currentBlock = 0;
        foreach (var chunk in Chunks)
        {
            action(chunk, currentBlock, chunk.Header.ChunkSize);
            currentBlock += chunk.Header.ChunkSize;
        }
    }

    /// <summary>
    /// Gets the length when written to disk (aligns with libsparse's sparse_file_len)
    /// </summary>
    public long GetLength(bool sparse, bool includeCrc)
    {
        if (!sparse)
        {
            return (long)Header.TotalBlocks * Header.BlockSize;
        }

        long length = SparseFormat.SparseHeaderSize;
        foreach (var chunk in Chunks)
        {
            length += chunk.Header.TotalSize;
        }

        var sumBlocks = (uint)Chunks.Sum(c => c.Header.ChunkSize);
        if (Header.TotalBlocks > sumBlocks)
        {
            length += SparseFormat.ChunkHeaderSize;
        }

        if (includeCrc)
        {
            length += SparseFormat.ChunkHeaderSize + 4;
        }

        return length;
    }

    /// <summary>
    /// Smart import: automatically determines if it's a Sparse or Raw image (aligns with libsparse's sparse_file_import_auto)
    /// </summary>
    public static SparseFile ImportAuto(string filePath, bool validateCrc = false, bool verbose = false)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ImportAuto(stream, validateCrc, verbose, filePath);
    }

    /// <summary>
    /// Smart import: automatically determines via stream (aligns with libsparse's sparse_file_import_auto)
    /// </summary>
    public static SparseFile ImportAuto(Stream stream, bool validateCrc = false, bool verbose = false, string? filePath = null)
    {
        var magicData = new byte[4];
        var pos = stream.CanSeek ? stream.Position : 0;
        if (stream.Read(magicData, 0, 4) == 4)
        {
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(magicData);
            if (stream.CanSeek)
            {
                stream.Seek(pos, SeekOrigin.Begin);
            }

            if (magic == SparseFormat.SparseHeaderMagic)
            {
                return FromStreamInternal(stream, filePath, validateCrc, verbose);
            }
        }

        if (filePath != null)
        {
            return FromRawFile(filePath, 4096, verbose);
        }

        // If no path is provided and it's not Sparse, treat it as a Raw stream
        var rawFile = new SparseFile(4096, stream.Length, verbose);
        rawFile.ReadFromStream(stream, SparseReadMode.Normal);
        return rawFile;
    }

    /// <summary>
    /// Reads from a raw file and sparsifies it (aligns with libsparse's sparse_file_read)
    /// </summary>
    public static SparseFile FromRawFile(string filePath, uint blockSize = 4096, bool verbose = false)
    {
        var fi = new FileInfo(filePath);
        var sparseFile = new SparseFile(blockSize, fi.Length, verbose);
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        sparseFile.ReadFromStream(fs, SparseReadMode.Normal);
        return sparseFile;
    }

    /// <summary>
    /// Reads from a stream and sparsifies it or parses the Sparse format (aligns with libsparse's sparse_file_read)
    /// </summary>
    public void ReadFromStream(Stream stream, SparseReadMode mode, bool validateCrc = false)
    {
        if (mode == SparseReadMode.Sparse)
        {
            var headerData = new byte[SparseFormat.SparseHeaderSize];
            if (stream.Read(headerData, 0, headerData.Length) != headerData.Length)
            {
                throw new InvalidDataException("Failed to read sparse header");
            }

            var importedHeader = SparseHeader.FromBytes(headerData);
            if (!importedHeader.IsValid())
            {
                throw new InvalidDataException("Invalid sparse header");
            }

            if (Header.BlockSize != importedHeader.BlockSize)
            {
                throw new ArgumentException("Imported sparse file block size does not match the current file");
            }

            if (Verbose)
            {
                SparseLogger.Info($"ReadFromStream (Sparse mode): BlockSize={importedHeader.BlockSize}, TotalBlocks={importedHeader.TotalBlocks}, TotalChunks={importedHeader.TotalChunks}");
            }

            stream.Seek(importedHeader.FileHeaderSize - SparseFormat.SparseHeaderSize, SeekOrigin.Current);

            var checksum = Crc32.Begin();
            var currentBlockStart = CurrentBlock;

            for (uint i = 0; i < importedHeader.TotalChunks; i++)
            {
                var chunkHeaderData = new byte[SparseFormat.ChunkHeaderSize];
                stream.ReadExactly(chunkHeaderData, 0, chunkHeaderData.Length);
                var chunkHeader = ChunkHeader.FromBytes(chunkHeaderData);

                if (Verbose)
                {
                    SparseLogger.Info($"Imported Chunk #{i}: Type=0x{chunkHeader.ChunkType:X4}, Size={chunkHeader.ChunkSize} blocks");
                }

                stream.Seek(importedHeader.ChunkHeaderSize - SparseFormat.ChunkHeaderSize, SeekOrigin.Current);

                var dataSize = (long)chunkHeader.TotalSize - importedHeader.ChunkHeaderSize;
                var expectedRawSize = (long)chunkHeader.ChunkSize * Header.BlockSize;

                var chunk = new SparseChunk(chunkHeader) { StartBlock = currentBlockStart };

                switch (chunkHeader.ChunkType)
                {
                    case SparseFormat.ChunkTypeRaw:
                        var rawData = new byte[dataSize];
                        stream.ReadExactly(rawData, 0, (int)dataSize);
                        if (validateCrc)
                        {
                            checksum = Crc32.Update(checksum, rawData);
                        }
                        chunk.DataProvider = new MemoryDataProvider(rawData);
                        Chunks.Add(chunk);
                        currentBlockStart += chunkHeader.ChunkSize;
                        break;
                    case SparseFormat.ChunkTypeFill:
                        var fillData = new byte[4];
                        stream.ReadExactly(fillData, 0, 4);
                        var fillValue = BinaryPrimitives.ReadUInt32LittleEndian(fillData);
                        if (validateCrc)
                        {
                            // Simplified CRC calculation
                            for (var j = 0; j < expectedRawSize / 4; j++)
                            {
                                checksum = Crc32.Update(checksum, fillData);
                            }
                        }
                        chunk.FillValue = fillValue;
                        Chunks.Add(chunk);
                        currentBlockStart += chunkHeader.ChunkSize;
                        if (dataSize > 4)
                        {
                            stream.Seek(dataSize - 4, SeekOrigin.Current);
                        }
                        break;
                    case SparseFormat.ChunkTypeDontCare:
                        if (validateCrc)
                        {
                            var zero4 = new byte[4];
                            for (var j = 0; j < expectedRawSize / 4; j++)
                            {
                                checksum = Crc32.Update(checksum, zero4);
                            }
                        }
                        Chunks.Add(chunk);
                        currentBlockStart += chunkHeader.ChunkSize;
                        break;
                    case SparseFormat.ChunkTypeCrc32:
                        var crcFileData = new byte[4];
                        stream.ReadExactly(crcFileData, 0, 4);
                        if (validateCrc)
                        {
                            var fileCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcFileData);
                            if (fileCrc != Crc32.Finish(checksum))
                            {
                                throw new InvalidDataException("CRC32 validation failed");
                            }
                        }
                        break;
                    default:
                        break;
                }
            }
            return;
        }

        // Normal or Hole mode: scan stream and sparsify
        var blockSize = Header.BlockSize;
        var bufferScan = new byte[blockSize];
        long currentPos = 0;
        var streamLen = stream.Length;
        long rawStart = -1;

        while (currentPos < streamLen)
        {
            if (stream.CanSeek)
            {
                stream.Position = currentPos;
            }
            var bytesRead = stream.Read(bufferScan, 0, (int)Math.Min(blockSize, streamLen - currentPos));
            if (bytesRead == 0)
            {
                break;
            }

            uint fillValue = 0;
            var isZero = IsZeroBlock(bufferScan, bytesRead);
            var isFill = !isZero && bytesRead == blockSize && IsFillBlock(bufferScan, out fillValue);

            if (isZero || isFill)
            {
                if (rawStart != -1)
                {
                    AddStreamChunk(stream, rawStart, (uint)(currentPos - rawStart));
                    rawStart = -1;
                }

                if (isZero)
                {
                    var zeroStart = currentPos;
                    currentPos += bytesRead;
                    while (currentPos < streamLen)
                    {
                        var innerRead = stream.Read(bufferScan, 0, (int)Math.Min(blockSize, streamLen - currentPos));
                        if (innerRead > 0 && IsZeroBlock(bufferScan, innerRead))
                        {
                            currentPos += innerRead;
                        }
                        else
                        {
                            break;
                        }
                    }
                    if (mode == SparseReadMode.Hole)
                    {
                        AddDontCareChunk(currentPos - zeroStart);
                    }
                    else
                    {
                        AddFillChunk(0, currentPos - zeroStart);
                    }
                }
                else
                {
                    var fillStart = currentPos;
                    var currentFillValue = fillValue;
                    currentPos += bytesRead;
                    while (currentPos < streamLen)
                    {
                        var innerRead = stream.Read(bufferScan, 0, (int)Math.Min(blockSize, streamLen - currentPos));
                        if (innerRead == blockSize && IsFillBlock(bufferScan, out var innerFill) && innerFill == currentFillValue)
                        {
                            currentPos += innerRead;
                        }
                        else
                        {
                            break;
                        }
                    }
                    AddFillChunk(currentFillValue, currentPos - fillStart);
                }
            }
            else
            {
                if (rawStart == -1)
                {
                    rawStart = currentPos;
                }
                currentPos += bytesRead;
            }
        }

        if (rawStart != -1)
        {
            AddStreamChunk(stream, rawStart, (uint)(streamLen - rawStart));
        }
    }

    private static bool IsZeroBlock(byte[] buffer, int length)
    {
        if (length == 0)
        {
            return true;
        }

        var span = buffer.AsSpan(0, length);
        var ulongSpan = MemoryMarshal.Cast<byte, ulong>(span);
        foreach (var v in ulongSpan)
        {
            if (v != 0)
            {
                return false;
            }
        }

        for (var i = ulongSpan.Length * 8; i < length; i++)
        {
            if (buffer[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFillBlock(byte[] buffer, out uint fillValue)
    {
        fillValue = 0;
        if (buffer.Length < 4)
        {
            return false;
        }

        var pattern = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        var span = buffer.AsSpan();
        var uintSpan = MemoryMarshal.Cast<byte, uint>(span);
        foreach (var v in uintSpan)
        {
            if (v != pattern)
            {
                return false;
            }
        }

        for (var i = uintSpan.Length * 4; i < buffer.Length; i++)
        {
            if (buffer[i] != (byte)(pattern >> (i % 4 * 8)))
            {
                return false;
            }
        }

        fillValue = pattern;
        return true;
    }
}
