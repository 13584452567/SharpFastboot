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
            if (DeviceHandle == (IntPtr)INVALID_HANDLE_VALUE || ReadBulkHandle == (IntPtr)INVALID_HANDLE_VALUE || WriteBulkHandle == (IntPtr)INVALID_HANDLE_VALUE)
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
            if (ReadBulkHandle == IntPtr.Zero)
                throw new Exception("Read handle is closed.");

            int xfer = (length > 1024 * 1024) ? 1024 * 1024 : length;

            IntPtr dataPtr = Marshal.AllocHGlobal(xfer);
            try
            {
                while (true)
                {
                    uint bytesRead;
                    if (ReadFile(ReadBulkHandle, dataPtr, (uint)xfer, out bytesRead, IntPtr.Zero))
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
            if (WriteBulkHandle == IntPtr.Zero)
                throw new Exception("Write handle is closed.");

            int totalWritten = 0;
            const int MAX_USBFS_BULK_SIZE = 1024 * 1024;

            IntPtr dataPtr = Marshal.AllocHGlobal(Math.Min(length, MAX_USBFS_BULK_SIZE));
            try
            {
                while (totalWritten < length)
                {
                    int xfer = Math.Min(MAX_USBFS_BULK_SIZE, length - totalWritten);
                    Marshal.Copy(data, totalWritten, dataPtr, xfer);

                    uint bytesTransfered;
                    if (WriteFile(WriteBulkHandle, dataPtr, (uint)xfer, out bytesTransfered, IntPtr.Zero))
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
            if (DeviceHandle != IntPtr.Zero && DeviceHandle != (IntPtr)(-1))
            {
                CloseHandle(DeviceHandle);
                DeviceHandle = IntPtr.Zero;
            }
            if (ReadBulkHandle != IntPtr.Zero && ReadBulkHandle != (IntPtr)(-1))
            {
                CloseHandle(ReadBulkHandle);
                ReadBulkHandle = IntPtr.Zero;
            }
            if (WriteBulkHandle != IntPtr.Zero && WriteBulkHandle != (IntPtr)(-1))
            {
                CloseHandle(WriteBulkHandle);
                WriteBulkHandle = IntPtr.Zero;
            }
        }
    }
}
