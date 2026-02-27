using LibLpSharp;
using LibSparseSharp;
using System.IO;

namespace SharpFastboot
{
    public class SuperFlashHelper
    {
        private FastbootUtil _fastboot;
        private SuperImageBuilder _builder;
        private string _mainPartition;

        public SuperFlashHelper(FastbootUtil fastboot, string mainPartition = "super", string? emptyImagePath = null)
        {
            _fastboot = fastboot;
            _mainPartition = mainPartition;
            
            ulong superSize = 0;

            if (!string.IsNullOrEmpty(emptyImagePath) && File.Exists(emptyImagePath))
            {
                try
                {
                    var metadata = MetadataReader.ReadFromImageFile(emptyImagePath);
                    var builder = MetadataBuilder.FromMetadata(metadata);
                    _builder = new SuperImageBuilder(builder);
                }
                catch
                {
                    _builder = CreateDefaultBuilder(ref superSize);
                }
            }
            else
            {
                _builder = CreateDefaultBuilder(ref superSize);
            }
        }

        private SuperImageBuilder CreateDefaultBuilder(ref ulong superSize)
        {
            // Get super partition size from device
            string sizeStr = _fastboot.GetPartitionSize(_mainPartition);
            if (!string.IsNullOrEmpty(sizeStr))
            {
                if (sizeStr.StartsWith("0x")) superSize = Convert.ToUInt64(sizeStr.Substring(2), 16);
                else superSize = Convert.ToUInt64(sizeStr);
            }

            if (superSize == 0) superSize = 1024L * 1024 * 1024 * 4; // Default 4GB if not found
            
            var builder = new SuperImageBuilder(superSize, 65536, 2);
            builder.AddGroup("default", superSize);
            return builder;
        }

        public void AddPartition(string name, string imagePath, string groupName = "default")
        {
            var info = new FileInfo(imagePath);
            var partition = _builder.FindPartition(name);
            if (partition == null)
            {
                 // Not in super_empty.img? Add it manually (unlikely for standard builds but possible)
                 _builder.AddPartition(name, (ulong)info.Length, groupName, MetadataFormat.LP_PARTITION_ATTR_READONLY, imagePath);
            }
            else
            {
                 // In super_empty.img? Just update its size and mapping
                 _builder.UpdatePartitionImage(name, (ulong)info.Length, imagePath);
            }
        }

        public void Flash()
        {
            _fastboot.NotifyCurrentStep($"Building optimized {_mainPartition} image (streaming)...");
            using (SparseFile superSparse = _builder.Build())
            {
                long maxDownloadSize = _fastboot.GetMaxDownloadSize();
                _fastboot.FlashSparseFile(_mainPartition, superSparse, maxDownloadSize);
            }
        }
    }
}
