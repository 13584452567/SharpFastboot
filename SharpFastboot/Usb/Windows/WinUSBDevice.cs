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
        private IntPtr FileHandle;
        private USBDeviceDescriptor USBDeviceDescriptor;
        private USBDeviceConfigDescriptor USBDeviceConfigDescriptor;
        private USBDeviceInterfaceDescriptor USBDeviceInterfaceDescriptor;

        public override Exception? CreateHandle()
        {
            IntPtr hUsb = SimpleCreateHandle(DevicePath, true);
            uint bytesTransfered;
            if (hUsb == (IntPtr)INVALID_HANDLE_VALUE)
                return new Win32Exception(Marshal.GetLastWin32Error());
            FileHandle = hUsb;
            if(!WinUsb_Initialize(hUsb, out WinUSBHandle))
                return new Win32Exception(Marshal.GetLastWin32Error());
            if (!WinUsb_GetCurrentAlternateSetting(WinUSBHandle, out InterfaceNum))
                return new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(USBDeviceDescriptor));
            if (!WinUsb_GetDescriptor(WinUSBHandle, USB_DEVICE_DESCRIPTOR_TYPE, 0, 0, ptr, (uint)Marshal.SizeOf(USBDeviceDescriptor), out bytesTransfered))
                return new Win32Exception(Marshal.GetLastWin32Error());
            USBDeviceDescriptor = Marshal.PtrToStructure<USBDeviceDescriptor>(ptr);
            Marshal.FreeHGlobal(ptr);
            ptr = Marshal.AllocHGlobal(Marshal.SizeOf(USBDeviceConfigDescriptor));
            if (!WinUsb_GetDescriptor(WinUSBHandle, USB_CONFIGURATION_DESCRIPTOR_TYPE, 0, 0, ptr, (uint)Marshal.SizeOf(USBDeviceConfigDescriptor), out bytesTransfered))
                return new Win32Exception(Marshal.GetLastWin32Error());
            USBDeviceConfigDescriptor = Marshal.PtrToStructure<USBDeviceConfigDescriptor>(ptr);
            Marshal.FreeHGlobal(ptr);
            if(!WinUsb_QueryInterfaceSettings(WinUSBHandle, InterfaceNum, out USBDeviceInterfaceDescriptor))
                return new Win32Exception(Marshal.GetLastWin32Error());

            if (USBDeviceInterfaceDescriptor.bInterfaceClass != 0xFF ||
                USBDeviceInterfaceDescriptor.bInterfaceSubClass != 0x42 ||
                USBDeviceInterfaceDescriptor.bInterfaceProtocol != 0x03)
            {
                return new Exception("Device interface does not match Fastboot protocol.");
            }

            if (USBDeviceInterfaceDescriptor.bNumEndpoints != 2)
            {
                return new Exception("Device interface must have exactly 2 endpoints.");
            }

            for (byte endpoint = 0; endpoint < USBDeviceInterfaceDescriptor.bNumEndpoints; endpoint++)
            {
                WinUSBPipeInfo pipeInfo;
                if(!WinUsb_QueryPipe(WinUSBHandle, InterfaceNum, endpoint, out pipeInfo))
                    return new Win32Exception(Marshal.GetLastWin32Error());
                if (pipeInfo.PipeType == WinUSBPipeType.UsbdPipeTypeBulk)
                {
                    if((pipeInfo.PipeID & USB_ENDPOINT_DIRECTION_MASK) != 0)
                    {
                        if (ReadBulkID == 0)
                        {
                            ReadBulkID = pipeInfo.PipeID;
                            ReadBulkIndex = endpoint;
                        }
                    }
                    else
                    {
                        if (WriteBulkID == 0)
                        {
                            WriteBulkID = pipeInfo.PipeID;
                            WriteBulkIndex = endpoint;
                        }
                    }
                }
            }
            GetSerialNumber();

            byte bTrue = 1;
            byte bFalse = 0;
            WinUsb_SetPipePolicy(WinUSBHandle, ReadBulkID, AUTO_CLEAR_STALL, 1, ref bTrue);
            WinUsb_SetPipePolicy(WinUSBHandle, WriteBulkID, AUTO_CLEAR_STALL, 1, ref bTrue);
            WinUsb_SetPipePolicy(WinUSBHandle, WriteBulkID, SHORT_PACKET_TERMINATE, 1, ref bFalse);

            return null;
        }

        public override Exception? GetSerialNumber()
        {
            uint bytes_get;
            uint descriptorSize = 64;
            IntPtr ptr = Marshal.AllocHGlobal((int)descriptorSize);
            while (!WinUsb_GetDescriptor(WinUSBHandle, USB_STRING_DESCRIPTOR_TYPE,
                USBDeviceDescriptor.iSerialNumber, 0x0409,
                ptr, descriptorSize, out bytes_get))
            {
                if((uint)Marshal.GetLastWin32Error() != (uint)ERROR_INSUFFICIENT_BUFFER)
                    return new Win32Exception(Marshal.GetLastWin32Error());
                descriptorSize *= 2;
                Marshal.FreeHGlobal(ptr);
                ptr = Marshal.AllocHGlobal((int)descriptorSize);
            }
            SerialNumber = Marshal.PtrToStringUni(ptr + 2, (int)(bytes_get - 2) / 2);
            Marshal.FreeHGlobal(ptr);
            return null;
        }

        public override byte[] Read(int length)
        {
            if (WinUSBHandle == IntPtr.Zero)
                throw new Exception("Device handle is closed.");

            int xfer = (length > 1024 * 1024) ? 1024 * 1024 : length;

            uint timeout = (uint)(500 + length * 8);
            WinUsb_SetPipePolicy(WinUSBHandle, ReadBulkID, PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref timeout);

            IntPtr dataPtr = Marshal.AllocHGlobal(xfer);
            try
            {
                while (true)
                {
                    uint bytesRead;
                    if (WinUsb_ReadPipe(WinUSBHandle, ReadBulkID, dataPtr, (uint)xfer, out bytesRead, IntPtr.Zero))
                    {
                        byte[] res = new byte[bytesRead];
                        Marshal.Copy(dataPtr, res, 0, (int)bytesRead);
                        return res;
                    }
                    else
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == 121) // ERROR_SEM_TIMEOUT
                        {
                            continue;
                        }
                        if (error == 6) // ERROR_INVALID_HANDLE
                        {
                            Dispose();
                        }
                        throw new Win32Exception(error);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(dataPtr);
            }
        }

        public override long Write(byte[] data, int length)
        {
            if (WinUSBHandle == IntPtr.Zero)
                throw new Exception("Device handle is closed.");

            int totalWritten = 0;
            const int MAX_USBFS_BULK_SIZE = 1024 * 1024;

            uint timeout = (uint)(500 + length * 8);
            WinUsb_SetPipePolicy(WinUSBHandle, WriteBulkID, PIPE_TRANSFER_TIMEOUT, sizeof(uint), ref timeout);

            IntPtr dataPtr = Marshal.AllocHGlobal(Math.Min(length, MAX_USBFS_BULK_SIZE));
            try
            {
                while (totalWritten < length)
                {
                    int xfer = Math.Min(MAX_USBFS_BULK_SIZE, length - totalWritten);
                    Marshal.Copy(data, totalWritten, dataPtr, xfer);
                    
                    uint bytesTransfered;
                    if (WinUsb_WritePipe(WinUSBHandle, WriteBulkID, dataPtr, (uint)xfer, out bytesTransfered, IntPtr.Zero))
                    {
                        if (bytesTransfered == 0) break;
                        totalWritten += (int)bytesTransfered;
                    }
                    else
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error == 6) // ERROR_INVALID_HANDLE
                        {
                            Dispose();
                        }
                        throw new Win32Exception(error);
                    }
                }
                return totalWritten;
            }
            finally
            {
                Marshal.FreeHGlobal(dataPtr);
            }
        }

        public override void Dispose()
        {
            if (WinUSBHandle != IntPtr.Zero)
            {
                WinUsb_Free(WinUSBHandle);
                WinUSBHandle = IntPtr.Zero;
            }
            if (FileHandle != IntPtr.Zero && FileHandle != (IntPtr)INVALID_HANDLE_VALUE)
            {
                CloseHandle(FileHandle);
                FileHandle = IntPtr.Zero;
            }
        }
    }
}
