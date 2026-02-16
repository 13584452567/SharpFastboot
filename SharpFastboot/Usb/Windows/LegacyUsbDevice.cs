using SharpFastboot.DataModel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static SharpFastboot.Usb.Windows.Win32API;

namespace SharpFastboot.Usb.Windows
{
    public class LegacyUsbDevice : UsbDevice
    {
        public static readonly uint IoGetSerialCode = ((FILE_DEVICE_UNKNOWN) << 16) | ((FILE_READ_ACCESS) << 14) | ((16) << 2) | (METHOD_BUFFERED);
        public IntPtr DeviceHandle { get; private set; }

        public IntPtr ReadBulkHandle { get; private set; }

        public IntPtr WriteBulkHandle { get; private set; }

        public override Exception? CreateHandle()
        {
            DeviceHandle = SimpleCreateHandle(DevicePath);
            ReadBulkHandle = SimpleCreateHandle(DevicePath + "\\BulkRead");
            WriteBulkHandle = SimpleCreateHandle(DevicePath + "\\BulkWrite");
            if (DeviceHandle == INVALID_HANDLE_VALUE || ReadBulkHandle == INVALID_HANDLE_VALUE || WriteBulkHandle == INVALID_HANDLE_VALUE)
                return new Win32Exception(Marshal.GetLastWin32Error());
            GetSerialNumber();
            return null;
        }

        public override Exception? GetSerialNumber()
        {
            IntPtr serialPtr = Marshal.AllocHGlobal(512);
            int bytes_get;
            if(DeviceIoControl(DeviceHandle, IoGetSerialCode, IntPtr.Zero, 0, serialPtr, 512, out bytes_get, IntPtr.Zero))
            {
                SerialNumber = Marshal.PtrToStringUni(serialPtr);
                Marshal.FreeHGlobal(serialPtr);
                return null;
            }
            return new Win32Exception(Marshal.GetLastWin32Error());
        }

        public override byte[] Read(int length)
        {
            int bytesRead;
            IntPtr dataPtr = Marshal.AllocHGlobal(length);
            if(ReadFile(ReadBulkHandle, dataPtr, length, out bytesRead, IntPtr.Zero))
            {
                byte[] data = new byte[bytesRead];
                Marshal.Copy(dataPtr, data, 0, bytesRead);
                Marshal.FreeHGlobal(dataPtr);
                return data;
            }
            Marshal.FreeHGlobal(dataPtr);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public override long Write(byte[] data, int length)
        {
            IntPtr dataPtr = Marshal.AllocHGlobal(length);
            ulong bytesWritten;
            Marshal.Copy(data, 0, dataPtr, length);
            if(WriteFile(WriteBulkHandle, dataPtr, length, out bytesWritten, IntPtr.Zero))
            {
                Marshal.FreeHGlobal(dataPtr);
                return (long)bytesWritten;
            }
            Marshal.FreeHGlobal(dataPtr);
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}
