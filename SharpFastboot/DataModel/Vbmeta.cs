using System.Runtime.InteropServices;
using System.Text;

namespace SharpFastboot.DataModel
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct VbmetaHeader
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] Magic;

        public uint RequiredLibavbVersionMajor;
        public uint RequiredLibavbVersionMinor;
        public uint AuthenticationDataBlockSize;
        public uint AuxiliaryDataBlockSize;
        public uint AlgorithmType;
        public ulong HashOffset;
        public ulong HashSize;
        public ulong SignatureOffset;
        public ulong SignatureSize;
        public ulong PublicKeyValueOffset;
        public ulong PublicKeyValueSize;
        public ulong PublicKeyMetadataOffset;
        public ulong PublicKeyMetadataSize;
        public ulong DescriptorsOffset;
        public ulong DescriptorsSize;
        public ulong RollbackIndex;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] Reserved0;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 47)]
        public byte[] ReleaseString;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 80)]
        public byte[] Reserved;

        public bool IsValid() => Encoding.ASCII.GetString(Magic) == "AVB0";

        public static VbmetaHeader FromBytes(byte[] data)
        {
            return DataHelper.Bytes2Struct<VbmetaHeader>(data, Marshal.SizeOf<VbmetaHeader>());
        }
    }

    public enum AvbAlgorithmType : uint
    {
        NONE = 0,
        SHA256_RSA2048 = 1,
        SHA256_RSA4096 = 2,
        SHA256_RSA8192 = 3,
        SHA512_RSA2048 = 4,
        SHA512_RSA4096 = 5,
        SHA512_RSA8192 = 6
    }
}
