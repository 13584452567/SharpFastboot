using SharpFastboot;
using SharpFastboot.Usb;
using SharpFastboot.Usb.Windows;
using System.Runtime.InteropServices;
using System.Text;
using LibSparseSharp;

namespace Demo
{
    static class Demo
    {
        static void Main(string[] args)
        {
            var result = WinUSBFinder.FindDevice();
            UsbDevice usb = result[0];
            FastbootUtil util = new FastbootUtil(usb);
            util.CurrentStepChanged += (sender, e) => Console.WriteLine(e);
            util.ReceivedFromDevice += (sender, e) => Console.WriteLine(e.NewInfo);
            var resp = util.OemCommand("lks");
            Console.WriteLine(resp.Result);
            //util.FlashSparseImage("super", "G:\\tools\\simg2img\\super.img");
            //util.Reboot();
        }
    }
}