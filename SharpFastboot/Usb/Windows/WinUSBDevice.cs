using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static SharpFastboot.Usb.Windows.Win32API;
using static SharpFastboot.Usb.Windows.WinUSBAPI;
using System.Runtime.InteropServices;
using System.ComponentModel;
using SharpFastboot.DataModel;

namespace SharpFastboot.Usb.Windows
{
    public class WinUSBDevice : UsbDevice
    {
        private byte InterfaceNum;
        private byte ReadBulkID, WriteBulkID;
        private byte ReadBulkIndex, WriteBulkIndex;
        private IntPtr WinUSBHandle;
        private USBDeviceDescriptor USBDeviceDescriptor;
        private USBDeviceConfigDescriptor USBDeviceConfigDescriptor;
        private USBDeviceInterfaceDescriptor USBDeviceInterfaceDescriptor;

        public override Exception? CreateHandle()
        {
            IntPtr hUsb = SimpleCreateHandle(DevicePath, true);
            int bytesTransfered;
            if (hUsb == INVALID_HANDLE_VALUE)
                return new Win32Exception(Marshal.GetLastWin32Error());
            if(!WinUsb_Initialize(hUsb, out WinUSBHandle))
                return new Win32Exception(Marshal.GetLastWin32Error());
            if (!WinUsb_GetCurrentAlternateSetting(WinUSBHandle, out InterfaceNum))
                return new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(USBDeviceDescriptor));
            if (!WinUsb_GetDescriptor(WinUSBHandle, USB_DEVICE_DESCRIPTOR_TYPE, 0, 0, ptr, Marshal.SizeOf(USBDeviceDescriptor), out bytesTransfered))
                return new Win32Exception(Marshal.GetLastWin32Error());
            USBDeviceDescriptor = Marshal.PtrToStructure<USBDeviceDescriptor>(ptr);
            Marshal.FreeHGlobal(ptr);
            ptr = Marshal.AllocHGlobal(Marshal.SizeOf(USBDeviceConfigDescriptor));
            if (!WinUsb_GetDescriptor(WinUSBHandle, USB_CONFIGURATION_DESCRIPTOR_TYPE, 0, 0, ptr, Marshal.SizeOf(USBDeviceConfigDescriptor), out bytesTransfered))
                return new Win32Exception(Marshal.GetLastWin32Error());
            USBDeviceConfigDescriptor = Marshal.PtrToStructure<USBDeviceConfigDescriptor>(ptr);
            Marshal.FreeHGlobal(ptr);
            if(!WinUsb_QueryInterfaceSettings(WinUSBHandle, InterfaceNum, out USBDeviceInterfaceDescriptor))
                return new Win32Exception(Marshal.GetLastWin32Error());
            for (byte endpoint = 0; endpoint < USBDeviceInterfaceDescriptor.bNumEndpoints; endpoint++)
            {
                WinUSBPipeInfo pipeInfo;
                if(!WinUsb_QueryPipe(WinUSBHandle, InterfaceNum, endpoint, out pipeInfo))
                    return new Win32Exception(Marshal.GetLastWin32Error());
                if (pipeInfo.PipeType == WinUSBPipeType.UsbdPipeTypeBulk)
                {
                    if((pipeInfo.PipeID & USB_ENDPOINT_DIRECTION_MASK) != 0){
                        ReadBulkID = pipeInfo.PipeID;
                        ReadBulkIndex = endpoint;
                    }
                    else
                    {
                        WriteBulkID = pipeInfo.PipeID;
                        WriteBulkIndex = endpoint;
                    }
                }
            }
            GetSerialNumber();
            return null;
        }

        public override Exception? GetSerialNumber()
        {
            int bytes_get;
            int descriptorSize = 64;
            IntPtr ptr = Marshal.AllocHGlobal(descriptorSize);
            while (!WinUsb_GetDescriptor(WinUSBHandle, USB_STRING_DESCRIPTOR_TYPE,
                USBDeviceDescriptor.iSerialNumber, 0x0409,
                ptr, descriptorSize, out bytes_get))
            {
                if(Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER)
                    return new Win32Exception(Marshal.GetLastWin32Error());
                descriptorSize *= 2;
                Marshal.FreeHGlobal(ptr);
                ptr = Marshal.AllocHGlobal(descriptorSize);
            }
            SerialNumber = Marshal.PtrToStringUni(ptr + 2)?.Substring(0, (bytes_get - 2) / 2);
            return new Win32Exception(Marshal.GetLastWin32Error());
        }

        public override byte[] Read(int length)
        {
            byte[] data = new byte[length];
            ulong bytesTransfered;
            if (WinUsb_ReadPipe(WinUSBHandle, ReadBulkID, data, (ulong)length, out bytesTransfered, IntPtr.Zero))
            {
                byte[] realData = new byte[bytesTransfered];
                Array.Copy(data, realData, (int)bytesTransfered);
                return realData;
            }
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public override long Write(byte[] data, int length)
        {
            ulong bytesTransfered;
            if (WinUsb_WritePipe(WinUSBHandle, WriteBulkID, data, (ulong)length, out bytesTransfered, IntPtr.Zero))
                return (long)bytesTransfered;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}
