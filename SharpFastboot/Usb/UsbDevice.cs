using SharpFastboot.DataModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SharpFastboot.Usb
{
    public abstract class UsbDevice
    {
        public required string DevicePath { get; set; }
        public string? SerialNumber { get; set; }
        public UsbDeviceType UsbDeviceType { get; set; }
        public abstract byte[] Read(int length);
        public abstract long Write(byte[] data, int length);
        public abstract Exception? GetSerialNumber();
        public abstract Exception? CreateHandle();
    }

    public enum UsbDeviceType
    {
        WinLegacy = 0,
        WinUSB = 1,
        Linux = 2,
        LibUSB = 3
    }
}
