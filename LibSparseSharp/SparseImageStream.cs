
namespace LibSparseSharp;

/// <summary>
/// 映射区块为完整稀疏镜像流
/// </summary>
public class SparseImageStream : Stream
{
    private readonly uint _blockSize;
    private readonly List<SparseChunk> _mappedChunks = [];
    private readonly List<Section> _sections = [];
    private readonly long _totalByteLength;
    private long _position;

    private struct Section
    {
        public long StartByteOffset;
        public long Length;
        public SectionType Type;
        public int ChunkIndex; // 仅对 ChunkHeader ChunkData 有效
        public byte[]? StaticData; // 用于 Header 缓存
    }

    private enum SectionType
    {
        SparseHeader,
        ChunkHeader,
        ChunkData,
        CrcHeader,
        CrcData
    }

    /// <summary>
    /// 构造映射流
    /// </summary>
    /// <param name="source">SparseFile</param>
    /// <param name="startBlock">映射起始块（绝对位置）</param>
    /// <param name="blockCount">本流包含的有效数据块</param>
    /// <param name="includeCrc">是否包含 CRC32 校验</param>
    /// <param name="fullRange">是否在header 中声明全部TotalBlocks 并使用skip 补齐起始/尾部（Resparse 用）</param>
    public SparseImageStream(SparseFile source, uint startBlock, uint blockCount, bool includeCrc = false, bool fullRange = true)
    {
        _blockSize = source.Header.BlockSize;

        MapChunks(source, startBlock, blockCount, fullRange);

        long currentByteOffset = 0;
        var totalChunks = (uint)_mappedChunks.Count;
        uint imageChecksum = 0;

        if (includeCrc)
        {
            totalChunks++;
            imageChecksum = CalculateChecksum();
        }

        var header = new SparseHeader
        {
            Magic = SparseFormat.SparseHeaderMagic,
            MajorVersion = source.Header.MajorVersion,
            MinorVersion = source.Header.MinorVersion,
            FileHeaderSize = SparseFormat.SparseHeaderSize,
            ChunkHeaderSize = SparseFormat.ChunkHeaderSize,
            BlockSize = _blockSize,
            TotalBlocks = fullRange ? source.Header.TotalBlocks : blockCount,
            TotalChunks = totalChunks,
            ImageChecksum = imageChecksum
        };
        var headerBytes = header.ToBytes();
        _sections.Add(new Section
        {
            StartByteOffset = 0,
            Length = headerBytes.Length,
            Type = SectionType.SparseHeader,
            StaticData = headerBytes
        });
        currentByteOffset += headerBytes.Length;

        for (var i = 0; i < _mappedChunks.Count; i++)
        {
            var chunk = _mappedChunks[i];
            var chunkHeaderBytes = chunk.Header.ToBytes();

            _sections.Add(new Section
            {
                StartByteOffset = currentByteOffset,
                Length = SparseFormat.ChunkHeaderSize,
                Type = SectionType.ChunkHeader,
                ChunkIndex = i,
                StaticData = chunkHeaderBytes
            });
            currentByteOffset += SparseFormat.ChunkHeaderSize;

            var dataSize = (long)chunk.Header.TotalSize - SparseFormat.ChunkHeaderSize;
            if (dataSize > 0)
            {
                _sections.Add(new Section
                {
                    StartByteOffset = currentByteOffset,
                    Length = dataSize,
                    Type = SectionType.ChunkData,
                    ChunkIndex = i
                });
                currentByteOffset += dataSize;
            }
        }

        if (includeCrc)
        {
            var crcHeader = new ChunkHeader
            {
                ChunkType = SparseFormat.ChunkTypeCrc32,
                Reserved = 0,
                ChunkSize = 0,
                TotalSize = SparseFormat.ChunkHeaderSize + 4
            };
            var crcHeaderBytes = crcHeader.ToBytes();
            _sections.Add(new Section
            {
                StartByteOffset = currentByteOffset,
                Length = crcHeaderBytes.Length,
                Type = SectionType.CrcHeader,
                StaticData = crcHeaderBytes
            });
            currentByteOffset += crcHeaderBytes.Length;

            var crcBytes = BitConverter.GetBytes(imageChecksum);
            _sections.Add(new Section
            {
                StartByteOffset = currentByteOffset,
                Length = crcBytes.Length,
                Type = SectionType.CrcData,
                StaticData = crcBytes
            });
            currentByteOffset += crcBytes.Length;
        }

        _totalByteLength = currentByteOffset;
    }

    private uint CalculateChecksum()
    {
        var checksum = Crc32.Begin();
        var buffer = new byte[1024 * 1024];

        foreach (var chunk in _mappedChunks)
        {
            var totalBytes = (long)chunk.Header.ChunkSize * _blockSize;
            switch (chunk.Header.ChunkType)
            {
                case SparseFormat.ChunkTypeRaw:
                    if (chunk.DataProvider != null)
                    {
                        long offset = 0;
                        while (offset < totalBytes)
                        {
                            var toProcess = (int)Math.Min(buffer.Length, totalBytes - offset);
                            var read = chunk.DataProvider.Read(offset, buffer, 0, toProcess);
                            if (read <= 0)
                            {
                                break;
                            }

                            checksum = Crc32.Update(checksum, buffer, 0, read);
                            offset += read;
                        }
                    }
                    else
                    {
                        var zeroBuf = new byte[Math.Min(buffer.Length, totalBytes)];
                        long processed = 0;
                        while (processed < totalBytes)
                        {
                            var toProcess = (int)Math.Min(zeroBuf.Length, totalBytes - processed);
                            checksum = Crc32.Update(checksum, zeroBuf, 0, toProcess);
                            processed += toProcess;
                        }
                    }
                    break;

                case SparseFormat.ChunkTypeFill:
                    var fillValData = BitConverter.GetBytes(chunk.FillValue);
                    // 填充缓冲区以进行批量 CRC
                    for (var i = 0; i <= buffer.Length - 4; i += 4)
                    {
                        Array.Copy(fillValData, 0, buffer, i, 4);
                    }

                    long processedFill = 0;
                    while (processedFill < totalBytes)
                    {
                        var toProcess = (int)Math.Min(buffer.Length, totalBytes - processedFill);
                        checksum = Crc32.Update(checksum, buffer, 0, toProcess);
                        processedFill += toProcess;
                    }
                    break;

                case SparseFormat.ChunkTypeDontCare:
                    Array.Clear(buffer, 0, buffer.Length); // 重用并清零缓冲区
                    long processedZero = 0;
                    while (processedZero < totalBytes)
                    {
                        var toProcess = (int)Math.Min(buffer.Length, totalBytes - processedZero);
                        checksum = Crc32.Update(checksum, buffer, 0, toProcess);
                        processedZero += toProcess;
                    }
                    break;
                default:
                    break;
            }
        }
        return Crc32.Finish(checksum);
    }

    private void MapChunks(SparseFile source, uint startBlock, uint blockCount, bool fullRange)
    {
        if (fullRange && startBlock > 0)
        {
            _mappedChunks.Add(new SparseChunk(new ChunkHeader
            {
                ChunkType = SparseFormat.ChunkTypeDontCare,
                ChunkSize = startBlock,
                TotalSize = SparseFormat.ChunkHeaderSize
            }));
        }

        uint currentSrcBlock = 0;
        var endBlock = startBlock + blockCount;

        foreach (var chunk in source.Chunks)
        {
            var chunkEnd = currentSrcBlock + chunk.Header.ChunkSize;

            if (chunkEnd > startBlock && currentSrcBlock < endBlock)
            {
                var intersectStart = Math.Max(startBlock, currentSrcBlock);
                var intersectEnd = Math.Min(endBlock, chunkEnd);
                var intersectCount = intersectEnd - intersectStart;

                var mappedChunk = CloneChunkSlice(chunk, intersectStart - currentSrcBlock, intersectCount);
                _mappedChunks.Add(mappedChunk);
            }

            currentSrcBlock = chunkEnd;
            if (currentSrcBlock >= endBlock)
            {
                break;
            }
        }

        if (fullRange && endBlock < source.Header.TotalBlocks)
        {
            _mappedChunks.Add(new SparseChunk(new ChunkHeader
            {
                ChunkType = SparseFormat.ChunkTypeDontCare,
                ChunkSize = source.Header.TotalBlocks - endBlock,
                TotalSize = SparseFormat.ChunkHeaderSize
            }));
        }
    }

    private SparseChunk CloneChunkSlice(SparseChunk original, uint offsetInBlocks, uint count)
    {
        var header = original.Header with
        {
            ChunkSize = count,
            TotalSize = original.Header.ChunkType == SparseFormat.ChunkTypeRaw
                ? SparseFormat.ChunkHeaderSize + (count * _blockSize)
                : original.Header.ChunkType == SparseFormat.ChunkTypeFill ? SparseFormat.ChunkHeaderSize + 4 : (uint)SparseFormat.ChunkHeaderSize
        };

        var newChunk = new SparseChunk(header) { FillValue = original.FillValue };

        if (original.DataProvider != null && header.ChunkType == SparseFormat.ChunkTypeRaw)
        {
            newChunk.DataProvider = new SubDataProvider(original.DataProvider, (long)offsetInBlocks * _blockSize, (long)count * _blockSize);
        }

        return newChunk;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _totalByteLength)
        {
            return 0;
        }

        var totalRead = 0;
        while (totalRead < count && _position < _totalByteLength)
        {
            var section = FindSectionAtOffset(_position);
            var offsetInSection = _position - section.StartByteOffset;
            var toRead = (int)Math.Min(count - totalRead, section.Length - offsetInSection);

            switch (section.Type)
            {
                case SectionType.SparseHeader:
                case SectionType.ChunkHeader:
                case SectionType.CrcHeader:
                case SectionType.CrcData:
                    Buffer.BlockCopy(section.StaticData!, (int)offsetInSection, buffer, offset + totalRead, toRead);
                    break;

                case SectionType.ChunkData:
                    var chunk = _mappedChunks[section.ChunkIndex];
                    if (chunk.Header.ChunkType == SparseFormat.ChunkTypeRaw)
                    {
                        chunk.DataProvider?.Read(offsetInSection, buffer, offset + totalRead, toRead);
                    }
                    else if (chunk.Header.ChunkType == SparseFormat.ChunkTypeFill)
                    {
                        var fillValue = chunk.FillValue;
                        for (var i = 0; i < toRead; i++)
                        {
                            var byteIdx = (int)((offsetInSection + i) % 4);
                            buffer[offset + totalRead + i] = (byte)(fillValue >> (byteIdx * 8));
                        }
                    }
                    break;
                default:
                    break;
            }

            _position += toRead;
            totalRead += toRead;
        }

        return totalRead;
    }

    private Section FindSectionAtOffset(long pos)
    {
        int low = 0, high = _sections.Count - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var sec = _sections[mid];
            if (pos >= sec.StartByteOffset && pos < sec.StartByteOffset + sec.Length)
            {
                return sec;
            }

            if (pos < sec.StartByteOffset)
            {
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }
        return _sections.Last();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        switch (origin)
        {
            case SeekOrigin.Begin: _position = offset; break;
            case SeekOrigin.Current: _position += offset; break;
            case SeekOrigin.End: _position = _totalByteLength + offset; break;
            default:
                break;
        }
        _position = Math.Clamp(_position, 0, _totalByteLength);
        return _position;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _totalByteLength;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private class SubDataProvider(ISparseDataProvider parent, long offset, long length) : ISparseDataProvider
    {
        public long Length => length;
        public int Read(long inOffset, byte[] buffer, int bufferOffset, int count) =>
            parent.Read(offset + inOffset, buffer, bufferOffset, (int)Math.Min(count, length - inOffset));
        public void WriteTo(Stream stream) => throw new NotSupportedException();
        public void Dispose() { }
        public ISparseDataProvider GetSubProvider(long subOffset, long subLength) =>
            new SubDataProvider(parent, offset + subOffset, subLength);
    }
}
