using SharpFastboot.DataModel;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static SharpFastboot.Usb.Windows.Win32API;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace SharpFastboot.Usb.Windows
{
    public class WinUSBFinder
    {
        private static GUID AndroidUsbGUID =
            new GUID
            {
                Data1 = 0xf72fe0d4,
                Data2 = 0xcbcb,
                Data3 = 0x407d,
                Data4 = [0x88, 0x14, 0x9e, 0xd6, 0x73, 0xd0, 0xdd, 0x6b]
            };

        public static readonly uint IoGetDescriptorCode = ((FILE_DEVICE_UNKNOWN) << 16) | ((FILE_READ_ACCESS) << 14) | ((10) << 2) | (METHOD_BUFFERED);

        public static List<UsbDevice> FindDevice()
        {
            List<UsbDevice> devices = new List<UsbDevice>();
            IntPtr devInfo = SetupDiGetClassDevsW(ref AndroidUsbGUID, null, 0, DIGCF_DEVICEINTERFACE);
            if (devInfo.ToInt64() == -1)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                uint index;
                for (index = 0; ; index++)
                {
                    SpDeviceInterfaceData interfaceData = new SpDeviceInterfaceData();
                    interfaceData.cbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>();
                    if (SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref AndroidUsbGUID, index, ref interfaceData))
                    {
                        uint sizeResult = GetInterfaceDetailDataRequiredSize(devInfo, interfaceData);
                        IntPtr buffer = Marshal.AllocHGlobal((int)sizeResult);
                        Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetailW(devInfo, ref interfaceData,
                            buffer, sizeResult, out _, IntPtr.Zero))
                        {
                            Marshal.FreeHGlobal(buffer);
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        }
                        else
                        {
                            string? devicePath = Marshal.PtrToStringUni(buffer + 4);
                            Marshal.FreeHGlobal(buffer);
                            if (string.IsNullOrEmpty(devicePath))
                                continue;
                            bool? isLegacy = isLegacyDevice(devicePath);
                            if (!isLegacy.HasValue)
                                continue;

                            UsbDevice usb;
                            if (isLegacy.Value)
                            {
                                usb = new LegacyUsbDevice { DevicePath = devicePath };
                                usb.UsbDeviceType = UsbDeviceType.WinLegacy;
                            }
                            else
                            {
                                usb = new WinUSBDevice { DevicePath = devicePath };
                                usb.UsbDeviceType = UsbDeviceType.WinUSB;
                            }

                            var err = usb.CreateHandle();
                            if (err == null)
                            {
                                devices.Add(usb);
                            }
                            else
                            {
                                usb.Dispose();
                            }
                        }
                    }
                    else
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == ERROR_NO_MORE_ITEMS) break;
                        throw new Win32Exception(error);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfo);
            }
            return devices;
        }

        private static uint GetInterfaceDetailDataRequiredSize(IntPtr devInfo, SpDeviceInterfaceData interfaceData)
        {
            uint requiredSize;
            if (!SetupDiGetDeviceInterfaceDetailW(devInfo, ref interfaceData, IntPtr.Zero, 0, out requiredSize, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ERROR_INSUFFICIENT_BUFFER)
                    return requiredSize;
                throw new Win32Exception(error);
            }
            //absolutely impossible
            throw new Win32Exception(ERROR_INSUFFICIENT_BUFFER);
        }

        private static bool? isLegacyDevice(string devicePath)
        {
            IntPtr hUsb = SimpleCreateHandle(devicePath);
            if (hUsb == (IntPtr)INVALID_HANDLE_VALUE)
                return null;
            IntPtr dataPtr = Marshal.AllocHGlobal(32);
            int bytes_get;
            bool ret = DeviceIoControl(hUsb, IoGetDescriptorCode, IntPtr.Zero, 0, dataPtr, 32, out bytes_get, IntPtr.Zero);
            Marshal.FreeHGlobal(dataPtr);
            CloseHandle(hUsb);
            return ret;
        } 
    }
}
