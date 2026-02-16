using LibSparseSharp;
using SharpFastboot.DataModel;
using SharpFastboot.Usb;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace SharpFastboot
{
    public class FastbootUtil
    {
        public UsbDevice UsbDevice { get; private set; }

        public FastbootUtil(UsbDevice usb) => UsbDevice = usb;
        public static int ReadTimeoutSeconds = 30;
        public static int OnceSendDataSize = 1024 * 1024;

        public event EventHandler<FastbootReceivedFromDeviceEventArgs>? ReceivedFromDevice;
        public event EventHandler<(long, long)>? SendDataProgressChanged;
        public event EventHandler<string>? CurrentStepChanged;

        /// <summary>
        /// 处理请求
        /// </summary>
        public FastbootResponse HandleResponse()
        {
            FastbootResponse response = new FastbootResponse();
            DateTime start = DateTime.Now;
            while ((DateTime.Now - start) < TimeSpan.FromSeconds(ReadTimeoutSeconds))
            {
                byte[] data = UsbDevice.Read(256);
                string devStatus = Encoding.UTF8.GetString(data);
                if (devStatus.StartsWith("OKAY") || devStatus.StartsWith("FAIL"))
                {
                    response.Result = devStatus.StartsWith("OKAY") ? FastbootState.Success : FastbootState.Fail;
                    response.Response = devStatus.Substring(4);
                    return response;
                }
                if (devStatus.StartsWith("INFO"))
                {
                    string info = devStatus.Substring(4);
                    response.Info.Add(info);
                    ReceivedFromDevice?.Invoke(this, new FastbootReceivedFromDeviceEventArgs(FastbootState.Info, info));
                    start = DateTime.Now;
                }
                else if (devStatus.StartsWith("TEXT"))
                {
                    string text = devStatus.Substring(4);
                    response.Text += text;
                    ReceivedFromDevice?.Invoke(this, new FastbootReceivedFromDeviceEventArgs(FastbootState.Text, null, text));
                    start = DateTime.Now;
                }
                else if (devStatus.StartsWith("DATA"))
                {
                    response.Result = FastbootState.Data;
                    int size = int.Parse(devStatus.Substring(4), System.Globalization.NumberStyles.HexNumber);
                    response.Size = size;
                    return response;
                }
                else
                {
                    response.Result = FastbootState.Unknown;
                    return response;
                }
            }
            response.Result = FastbootState.Timeout;
            return response;
        }

        /// <summary>
        /// 发送命令
        /// </summary>
        public FastbootResponse RawCommand(string command)
        {
            UsbDevice.Write(Encoding.UTF8.GetBytes(command), command.Length);
            return HandleResponse();
        }

        public FastbootResponse Reboot(string target = "system")
        {
            return RawCommand("reboot:" + target).ThrowIfError();
        }

        /// <summary>
        /// 获取所有属性
        /// </summary>
        public Dictionary<string,string> GetVarAll()
        {
            return RawCommand("getvar:all")
                .ThrowIfError()
                .Info.ToDictionary(str => str.Substring(0, str.LastIndexOf(":")),
                                    str => str.Substring(str.LastIndexOf(":") + 2).TrimStart());
        }

        /// <summary>
        /// 获取单个属性
        /// </summary>
        public string GetVar(string key) => RawCommand("getvar:" + key).ThrowIfError().Response;

        /// <summary>
        /// 获取插槽个数
        /// </summary>
        public int GetSlotCount()
        {
            int slot_count = 1;
            string count = GetVar("slot-count");
            int.TryParse(count, out slot_count);
            return slot_count;
        }

        /// <summary>
        /// 下载数据
        /// </summary>
        public FastbootResponse DownloadData(byte[] data)
        {
            FastbootResponse response = RawCommand("download:" + data.Length.ToString("x8"));
            if (response.Result == FastbootState.Fail)
                return response;
            UsbDevice.Write(data, data.Length);
            return HandleResponse();
        }

        /// <summary>
        /// 下载数据
        /// </summary>
        public FastbootResponse DownloadData(Stream stream, long length, bool onEvent = true)
        {
            FastbootResponse response = RawCommand("download:" + length.ToString("x8"));
            if (response.Result == FastbootState.Fail)
                return response;
            byte[] buffer = new byte[OnceSendDataSize];
            long bytesRead = 0;
            while (true)
            {
                int readSize = stream.Read(buffer, 0, buffer.Length);
                if (readSize <= 0) break;
                UsbDevice.Write(buffer, readSize);
                bytesRead += readSize;
                if(onEvent)
                    SendDataProgressChanged?.Invoke(this, (bytesRead, length));
            }
            return HandleResponse();
        }

        /// <summary>
        /// 刷入非稀疏镜像
        /// </summary>
        public FastbootResponse FlashUnsparseImage(string partition, Stream stream, long length)
        {
            CurrentStepChanged?.Invoke(this, $"Sending {partition}");
            FastbootResponse response = DownloadData(stream, length).ThrowIfError();
            CurrentStepChanged?.Invoke(this, $"Flashing {partition}");
            return RawCommand("flash:" + partition).ThrowIfError();
        }

        /// <summary>
        /// 刷入稀疏镜像
        /// </summary>
        public FastbootResponse FlashSparseImage(string partition, string filePath)
        {
            int count = 1;
            FastbootResponse response = new FastbootResponse();
            int max_download_size = int.Parse(GetVar("max-download-size").TrimStart("0x"), 
                System.Globalization.NumberStyles.HexNumber);
            SparseFile sfile = SparseFile.FromImageFile(filePath);
            var parts = sfile.Resparse(max_download_size);
            foreach(var item in parts)
            {
                Stream stream = item.GetExportStream(0, item.Header.TotalBlocks);
                CurrentStepChanged?.Invoke(this, $"Sending {partition}({count} / {parts.Count})");
                DownloadData(stream, stream.Length).ThrowIfError();
                CurrentStepChanged?.Invoke(this, $"Flashing {partition}({count} / {parts.Count})");
                response = RawCommand("flash:" + partition);
                response.ThrowIfError();
                count++;
            }
            return response;
        }

        public string GetCurrentSlot() => GetVar("current-slot");

        public FastbootResponse SetActiveSlot(string slot) => RawCommand("set_active:" + slot).ThrowIfError();
        public void ErasePartition(string partition) => RawCommand("erase:" + partition).ThrowIfError();


    }
}
