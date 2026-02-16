namespace LibLpSharp;

public static class Utility
{
    public static uint GetTotalMetadataSize(uint metadataMaxSize, uint maxSlots)
    {
        return MetadataFormat.LP_PARTITION_RESERVED_BYTES +
               ((MetadataFormat.LP_METADATA_GEOMETRY_SIZE + (metadataMaxSize * maxSlots)) * 2);
    }

    public static string GetSlotSuffix(uint slotNumber) => slotNumber == 0 ? "_a" : "_b";

    public static ulong AlignTo(ulong value, uint alignment)
    {
        if (alignment == 0)
        {
            return value;
        }


        var remainder = value % alignment;
        return remainder == 0 ? value : value + (alignment - remainder);
    }

    public struct FilesystemInfo
    {
        public string Type;
        public ulong Size;
    }

    public static FilesystemInfo DetectFilesystem(Stream stream, ulong partitionStartOffset)
    {
        try
        {
            var buffer = new byte[4096];
            stream.Seek((long)partitionStartOffset, SeekOrigin.Begin);
            if (stream.Read(buffer, 0, buffer.Length) < buffer.Length)
            {
                return new FilesystemInfo { Type = "Unknown", Size = 0 };
            }

            // SquashFS (Magic at offset 0)
            if (BitConverter.ToUInt32(buffer, 0) == 0x73717368) // 'hsqs'
            {
                return new FilesystemInfo
                {
                    Type = "SquashFS",
                    Size = BitConverter.ToUInt64(buffer, 40) // bytes_used
                };
            }

            // Superblock based filesystems (usually at offset 1024)
            var sb = buffer.AsSpan(1024);

            // EROFS
            if (BitConverter.ToUInt32(sb[0..4]) == 0xE0F5E1E2)
            {
                var log2_blksz = sb[12];
                var blocks = BitConverter.ToUInt32(sb[44..48]);
                if (log2_blksz == 0)
                {
                    log2_blksz = 12; // Default to 4KB if unset
                }

                return new FilesystemInfo
                {
                    Type = "EROFS",
                    Size = (ulong)blocks << log2_blksz
                };
            }

            // EXT2/3/4
            if (BitConverter.ToUInt16(sb[0x38..0x3A]) == 0xEF53)
            {
                return new FilesystemInfo
                {
                    Type = "EXT4",
                    Size = (ulong)BitConverter.ToUInt32(sb[0x4..0x8]) * (1024u << (int)BitConverter.ToUInt32(sb[0x18..0x1C]))
                };
            }

            // F2FS
            if (BitConverter.ToUInt32(sb[0..4]) == 0xF2F52010)
            {
                return new FilesystemInfo
                {
                    Type = "F2FS",
                    Size = (ulong)BitConverter.ToUInt32(sb[0x48..0x4C]) * 4096
                };
            }

            // VFAT / FAT32 (Check boot sector signature)
            if (buffer[510] == 0x55 && buffer[511] == 0xAA)
            {
                return new FilesystemInfo
                {
                    Type = "FAT/MBR",
                    Size = 0
                };
            }
        }
        catch (Exception ex)
        {
            LpLogger.Error($"Error detecting filesystem: {ex.Message}");
        }

        return new FilesystemInfo { Type = "Unknown", Size = 0 };
    }

    public static ulong DetectFilesystemSize(Stream stream, ulong partitionStartOffset) => DetectFilesystem(stream, partitionStartOffset).Size;
}
