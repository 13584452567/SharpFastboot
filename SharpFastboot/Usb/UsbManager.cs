using System.Runtime.InteropServices;
using SharpFastboot.Usb.libusbdotnet;
using SharpFastboot.Usb.Linux;
using SharpFastboot.Usb.macOS;
using SharpFastboot.Usb.Windows;

namespace SharpFastboot.Usb
{
    public static class UsbManager
    {
        public static bool ForceLibUsb { get; set; } = false;

        public static List<UsbDevice> GetAllDevices()
        {
            if (ForceLibUsb)
            {
                return LibUsbFinder.FindDevice();
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return WinUSBFinder.FindDevice();
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return LinuxUsbFinder.FindDevice();
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return MacOSUsbFinder.FindDevice();
            }
            // Fallback to libusb
            return LibUsbFinder.FindDevice();
        }
    }
}
