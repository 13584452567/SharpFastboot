using SharpFastboot;
using SharpFastboot.Usb;
using SharpFastboot.Usb.Windows;
using SharpFastboot.DataModel;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace FastbootCLI
{
    class Program
    {
        private static string? serial = null;
        private static string? slot = null;
        private static bool wipe = false;

        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                ShowHelp();
                return;
            }

            int i = 0;
            while (i < args.Length && args[i].StartsWith("-"))
            {
                string arg = args[i++];
                if (arg == "-s" && i < args.Length)
                {
                    serial = args[i++];
                }
                else if (arg == "-w")
                {
                    wipe = true;
                }
                else if (arg == "--slot" && i < args.Length)
                {
                    slot = args[i++];
                }
                else if (arg == "--version")
                {
                    ShowVersion();
                    return;
                }
                else if (arg == "-h" || arg == "--help")
                {
                    ShowHelp();
                    return;
                }
                else
                {
                    i--;
                    break;
                }
            }

            if (i >= args.Length)
            {
                ShowHelp();
                return;
            }

            string command = args[i++];
            List<string> commandArgs = args.Skip(i).ToList();

            try
            {
                ExecuteCommand(command, commandArgs);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAILED: " + ex.Message);
                Environment.Exit(1);
            }
        }

        static void ExecuteCommand(string command, List<string> args)
        {
            if (command == "devices")
            {
                ListDevices();
                return;
            }

            if (command == "version")
            {
                ShowVersion();
                return;
            }

            if (command == "help")
            {
                ShowHelp();
                return;
            }

            FastbootUtil? util = ConnectDevice();
            if (util == null)
            {
                throw new Exception("no devices found" + (serial != null ? " with serial " + serial : ""));
            }

            util.ReceivedFromDevice += (s, e) =>
            {
                if (e.NewInfo != null)
                {
                    Console.Error.WriteLine("(bootloader) " + e.NewInfo);
                }
            };

            util.CurrentStepChanged += (s, e) => Console.Error.WriteLine(e + "...");

            // If slot is specified, we might want to ensure we're targeting it
            // However, we'll let FastbootUtil handle its defaults for now unless we explicitly override.

            Stopwatch sw = Stopwatch.StartNew();

            switch (command)
            {
                case "getvar":
                    if (args.Count == 0) throw new Exception("getvar requires a variable name");
                    if (args[0] == "all")
                    {
                        var vars = util.GetVarAll();
                        foreach (var kv in vars) Console.WriteLine(kv.Key + ": " + kv.Value);
                    }
                    else
                    {
                        try {
                            Console.WriteLine(args[0] + ": " + util.GetVar(args[0]));
                        } catch {
                            // Some vars might return FAIL if not supported, but fastboot usually shows blank or error
                            Console.WriteLine(args[0] + ": ");
                        }
                    }
                    break;
                case "reboot":
                    if (args.Count == 0 || args[0] == "system") util.RawCommand("reboot");
                    else if (args[0] == "bootloader") util.RebootBootloader();
                    else if (args[0] == "recovery") util.Reboot("recovery");
                    else if (args[0] == "fastboot") util.Reboot("fastboot");
                    else util.RawCommand("reboot-" + args[0]);
                    break;
                case "reboot-bootloader":
                    util.RebootBootloader();
                    break;
                case "reboot-recovery":
                    util.Reboot("recovery");
                    break;
                case "reboot-fastboot":
                    util.Reboot("fastboot");
                    break;
                case "flash":
                    {
                        if (args.Count < 1) throw new Exception("flash: usage: flash <partition> [ <filename> ]");
                        string part = args[0];
                        string? file = args.Count > 1 ? args[1] : null;
                        if (file == null) throw new Exception("flash: filename required (auto-discovery not implemented)");
                        
                        string target = part;
                        if (slot != null && util.HasSlot(part)) target = part + "_" + slot;
                        
                        util.FlashImage(target, file);
                    }
                    break;
                case "erase":
                    if (args.Count == 0) throw new Exception("erase: usage: erase <partition>");
                    {
                        string target = args[0];
                        if (slot != null && util.HasSlot(args[0])) target = args[0] + "_" + slot;
                        util.ErasePartition(target);
                    }
                    break;
                case "format":
                    // format[:[<fs-type>][:[<size>]]] <partition>
                    if (args.Count == 0) throw new Exception("format: usage: format <partition>");
                    {
                        string target = args[0];
                        if (target.Contains(":")) target = target.Split(':').Last(); // Very simple parsing
                        if (slot != null && util.HasSlot(target)) target = target + "_" + slot;
                        util.FormatPartition(target);
                    }
                    break;
                case "continue":
                    util.Continue();
                    break;
                case "set_active":
                    if (args.Count == 0) throw new Exception("set_active: usage: set_active <slot>");
                    util.SetActiveSlot(args[0]);
                    break;
                case "oem":
                    if (args.Count == 0) throw new Exception("oem: usage: oem <command>");
                    util.OemCommand(string.Join(" ", args));
                    break;
                case "flashing":
                    if (args.Count == 0) throw new Exception("flashing: usage: flashing <subcommand>");
                    util.FlashingCommand(string.Join("_", args));
                    break;
                case "snapshot-update":
                    if (args.Count == 0) util.SnapshotUpdate();
                    else util.SnapshotUpdate(args[0]);
                    break;
                case "fetch":
                    if (args.Count < 2) throw new Exception("fetch: usage: fetch <partition> <filename>");
                    util.Fetch(args[0], args[1]);
                    break;
                case "get_staged":
                    if (args.Count == 0) throw new Exception("get_staged: usage: get_staged <filename>");
                    util.GetStaged(args[0]);
                    break;
                case "create-logical-partition":
                    if (args.Count < 2) throw new Exception("create-logical-partition: usage: create-logical-partition <name> <size>");
                    util.CreateLogicalPartition(args[0], ParseSize(args[1]));
                    break;
                case "delete-logical-partition":
                    if (args.Count == 0) throw new Exception("delete-logical-partition: usage: delete-logical-partition <name>");
                    util.DeleteLogicalPartition(args[0]);
                    break;
                case "resize-logical-partition":
                    if (args.Count < 2) throw new Exception("resize-logical-partition: usage: resize-logical-partition <name> <size>");
                    util.ResizeLogicalPartition(args[0], ParseSize(args[1]));
                    break;
                case "boot":
                case "flashall":
                case "update":
                case "stage":
                    Console.Error.WriteLine($"{command} command is not implemented in current library version");
                    break;
                default:
                    throw new Exception("unknown command: " + command);
            }


            if (wipe)
            {
                Console.Error.WriteLine("Wiping userdata and cache...");
                try { util.FormatPartition("userdata"); } catch { }
                try { util.FormatPartition("cache"); } catch { }
            }

            sw.Stop();
            Console.Error.WriteLine($"Finished. Total time: {sw.Elapsed.TotalSeconds:F3}s");
        }

        static long ParseSize(string sizeStr)
        {
            if (sizeStr.EndsWith("K", StringComparison.OrdinalIgnoreCase)) return long.Parse(sizeStr.Substring(0, sizeStr.Length - 1)) * 1024;
            if (sizeStr.EndsWith("M", StringComparison.OrdinalIgnoreCase)) return long.Parse(sizeStr.Substring(0, sizeStr.Length - 1)) * 1024 * 1024;
            if (sizeStr.EndsWith("G", StringComparison.OrdinalIgnoreCase)) return long.Parse(sizeStr.Substring(0, sizeStr.Length - 1)) * 1024 * 1024 * 1024;
            return long.Parse(sizeStr);
        }

        static void ListDevices()
        {
            var devices = WinUSBFinder.FindDevice();
            foreach (var dev in devices)
            {
                dev.GetSerialNumber();
                Console.WriteLine($"{(dev.SerialNumber ?? "unknown")}\tfastboot");
            }
        }

        static FastbootUtil? ConnectDevice()
        {
            var devices = WinUSBFinder.FindDevice();
            if (devices.Count == 0) return null;

            UsbDevice? target = null;
            if (serial != null)
            {
                target = devices.FirstOrDefault(d => { d.GetSerialNumber(); return d.SerialNumber == serial; });
            }
            else
            {
                target = devices[0];
            }

            if (target == null) return null;
            return new FastbootUtil(target);
        }

        static void ShowHelp()
        {
            Console.WriteLine("usage: fastboot [ <option> ] <command>");
            Console.WriteLine("");
            Console.WriteLine("commands:");
            Console.WriteLine("  update <filename>                        Reflash device from update.zip [NOT IMPLEMENTED]");
            Console.WriteLine("  flashall                                 Flash boot, system, and if found, recovery [NOT IMPLEMENTED]");
            Console.WriteLine("  flash <partition> [ <filename> ]         Write a file to a flash partition");
            Console.WriteLine("  erase <partition>                        Erase a flash partition");
            Console.WriteLine("  format <partition>                       Format a flash partition");
            Console.WriteLine("  getvar <variable>                        Display a bootloader variable");
            Console.WriteLine("  boot <kernel> [ <ramdisk> ]              Download and boot kernel [NOT IMPLEMENTED]");
            Console.WriteLine("  continue                                 Continue with the boot protocol");
            Console.WriteLine("  reboot [bootloader|recovery|fastboot]    Reboot device");
            Console.WriteLine("  reboot-bootloader                        Reboot device into bootloader");
            Console.WriteLine("  help                                     Show this help message");
            Console.WriteLine("");
            Console.WriteLine("options:");
            Console.WriteLine("  -w                                       Erase userdata and cache");
            Console.WriteLine("  -s <serial>                              Specify device serial number");
            Console.WriteLine("  --slot <slot>                            Specify slot name");
            Console.WriteLine("  --version                                Show version");
        }

        static void ShowVersion()
        {
            Console.WriteLine("fastboot version 1.0.0 (SharpFastboot)");
        }
    }
}
