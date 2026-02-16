using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static SharpFastboot.Usb.Windows.Win32API;

namespace SharpFastboot.Usb.Windows
{
    public class WinUSBAPI
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct WinUSBPipeInfo
        {
            public WinUSBPipeType PipeType;
            public byte PipeID;
            public ushort MaximumPacketSize;
            public byte Interval;
        }

        public enum WinUSBPipeType
        {
            UsbdPipeTypeControl,
            UsbdPipeTypeIsochronous,
            UsbdPipeTypeBulk,
            UsbdPipeTypeInterrupt
        }

        public static readonly byte USB_DEVICE_DESCRIPTOR_TYPE = 0x01;
        public static readonly byte USB_CONFIGURATION_DESCRIPTOR_TYPE = 0x02;
        public static readonly byte USB_ENDPOINT_DIRECTION_MASK = 0x80;
        public static readonly byte USB_STRING_DESCRIPTOR_TYPE = 0x03;

        [DllImport("Winusb.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern bool WinUsb_Initialize(IntPtr DeviceHandle, out IntPtr InterfaceHandle);

        [DllImport("Winusb.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern bool WinUsb_GetCurrentAlternateSetting(IntPtr InterfaceHandle, out byte InterfaceNum);

        [DllImport("Winusb.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern bool WinUsb_GetDescriptor(IntPtr DeviceHandle, byte DescriptorType, byte index, ushort LangID, 
            IntPtr buffer, int bufferLen, out int lengthTransfered);

        [DllImport("Winusb.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern bool WinUsb_QueryInterfaceSettings(IntPtr DeviceHandle, byte interfaceNum, out Win32API.USBDeviceInterfaceDescriptor descriptor);

        [DllImport("Winusb.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern bool WinUsb_QueryPipe(IntPtr DeviceHandle, byte interfaceNum, byte pipeIndex, out WinUSBPipeInfo info);

        [DllImport("Winusb.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern bool WinUsb_WritePipe(IntPtr DeviceHandle, byte pipeID, byte[] buffer,
            ulong bufferLen, out ulong bytesTransfered, IntPtr overlapp);

        [DllImport("Winusb.dll", CharSet = CharSet.Auto, CallingConvention = CallingConvention.StdCall, SetLastError = true)]
        public static extern bool WinUsb_ReadPipe(IntPtr DeviceHandle, byte pipeID, byte[] buffer,
            ulong bufferLen, out ulong bytesTransfered, IntPtr overlapp);

    }
}
