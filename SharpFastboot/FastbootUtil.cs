using LibSparseSharp;
using SharpFastboot.DataModel;
using SharpFastboot.Usb;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace SharpFastboot
{
    public class FastbootUtil
    {
        public UsbDevice UsbDevice { get; private set; }
        private Dictionary<string, string> _varCache = new Dictionary<string, string>();
        private Dictionary<string, bool> _hasSlotCache = new Dictionary<string, bool>();

        public FastbootUtil(UsbDevice usb) => UsbDevice = usb;
        public static int ReadTimeoutSeconds = 30;
        public static int OnceSendDataSize = 1024 * 1024;
        public static int SparseMaxDownloadSize = 256 * 1024 * 1024;

        private static readonly string[] PartitionPriority = {
            "boot", "dtbo", "init_boot", "vendor_boot", "pvmfw",
            "vbmeta", "vbmeta_system", "vbmeta_vendor", "vbmeta_custom",
            "recovery", "system", "vendor", "product", "system_ext", "odm", "vendor_dlkm", "odm_dlkm", "system_dlkm"
        };

        public event EventHandler<FastbootReceivedFromDeviceEventArgs>? ReceivedFromDevice;
        public event EventHandler<(long, long)>? DataTransferProgressChanged;
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
                byte[] data;
                try
                {
                    data = UsbDevice.Read(256);
                }
                catch (Exception e)
                {
                    response.Result = FastbootState.Fail;
                    response.Response = "status read failed: " + e.Message;
                    return response;
                }

                if (data.Length == 0) continue;

                string devStatus = Encoding.UTF8.GetString(data);
                if (devStatus.Length < 4)
                {
                    response.Result = FastbootState.Fail;
                    response.Response = "status malformed";
                    return response;
                }

                string prefix = devStatus.Substring(0, 4);
                string content = devStatus.Substring(4);

                if (prefix == "OKAY" || prefix == "FAIL")
                {
                    response.Result = prefix == "OKAY" ? FastbootState.Success : FastbootState.Fail;
                    response.Response = content;
                    return response;
                }
                else if (prefix == "INFO")
                {
                    response.Info.Add(content);
                    ReceivedFromDevice?.Invoke(this, new FastbootReceivedFromDeviceEventArgs(FastbootState.Info, content));
                    start = DateTime.Now;
                }
                else if (prefix == "TEXT")
                {
                    response.Text += content;
                    ReceivedFromDevice?.Invoke(this, new FastbootReceivedFromDeviceEventArgs(FastbootState.Text, null, content));
                    start = DateTime.Now;
                }
                else if (prefix == "DATA")
                {
                    response.Result = FastbootState.Data;
                    response.DataSize = long.Parse(content, System.Globalization.NumberStyles.HexNumber);
                    return response;
                }
                else
                {
                    response.Result = FastbootState.Unknown;
                    response.Response = devStatus;
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
            byte[] cmdBytes = Encoding.UTF8.GetBytes(command);
            try
            {
                if (UsbDevice.Write(cmdBytes, cmdBytes.Length) != cmdBytes.Length)
                    return new FastbootResponse { Result = FastbootState.Fail, Response = "command write failed (short transfer)" };
            }
            catch (Exception e)
            {
                return new FastbootResponse { Result = FastbootState.Fail, Response = "command write failed: " + e.Message };
            }
            return HandleResponse();
        }

        /// <summary>
        /// 等待设备连接 (阻塞调用)
        /// </summary>
        /// <param name="deviceFinder">发现设备的方法，如 FastbootCLI 中的 GetAllDevices</param>
        /// <param name="serial">可选：指定序列号</param>
        /// <param name="timeoutSeconds">超时时长（秒），-1 表示永久等待</param>
        public static FastbootUtil? WaitForDevice(Func<List<UsbDevice>> deviceFinder, string? serial = null, int timeoutSeconds = -1)
        {
            DateTime start = DateTime.Now;
            while (timeoutSeconds == -1 || (DateTime.Now - start).TotalSeconds < timeoutSeconds)
            {
                var devices = deviceFinder();
                UsbDevice? found = null;
                if (string.IsNullOrEmpty(serial))
                {
                    found = devices.FirstOrDefault();
                }
                else
                {
                    found = devices.FirstOrDefault(d =>
                    {
                        try { d.GetSerialNumber(); return d.SerialNumber == serial; }
                        catch { return false; }
                    });
                }

                if (found != null)
                {
                    foreach (var d in devices) if (d != found) d.Dispose();
                    return new FastbootUtil(found);
                }

                foreach (var d in devices) d.Dispose();

                Thread.Sleep(500);
            }
            return null;
        }

        public FastbootResponse Reboot(string target = "system")
        {
            if (target == "recovery") return RawCommand("reboot-recovery");
            if (target == "bootloader") return RawCommand("reboot-bootloader");
            if (target == "fastboot") return RawCommand("reboot-fastboot");
            if (target == "system") return RawCommand("reboot");
            return RawCommand("reboot-" + target);
        }

        /// <summary>
        /// 是否处于 fastbootd (userspace) 模式
        /// </summary>
        public bool IsUserspace()
        {
            try { return GetVar("is-userspace") == "yes"; } catch { return false; }
        }

        /// <summary>
        /// 执行 GSI 相关命令
        /// </summary>
        public FastbootResponse GsiCommand(string subCmd) => RawCommand("gsi:" + subCmd);

        /// <summary>
        /// 获取所有属性
        /// </summary>
        public Dictionary<string, string> GetVarAll()
        {
            _varCache.Clear();
            var res = RawCommand("getvar:all").ThrowIfError();
            var dict = new Dictionary<string, string>();
            foreach (var line in res.Info)
            {
                int colonIdx = line.LastIndexOf(":");
                if (colonIdx > 0)
                {
                    string k = line.Substring(0, colonIdx).Trim();
                    string v = line.Substring(colonIdx + 1).TrimStart();
                    dict[k] = v;
                    _varCache[k] = v;
                }
            }
            return dict;
        }

        /// <summary>
        /// 获取单个属性（带缓存）
        /// </summary>
        public string GetVar(string key)
        {
            if (_varCache.TryGetValue(key, out string? cached)) return cached;
            var res = RawCommand("getvar:" + key).ThrowIfError().Response;
            _varCache[key] = res;
            return res;
        }

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
            var res = HandleResponse();
            if (res.Result == FastbootState.Success)
            {
                using var sha256 = SHA256.Create();
                byte[] hash = sha256.ComputeHash(data);
                res.Hash = BitConverter.ToString(hash).Replace("-", "").ToLower();
            }
            return res;
        }

        /// <summary>
        /// 下载数据
        /// </summary>
        public FastbootResponse DownloadData(Stream stream, long length, bool onEvent = true)
        {
            FastbootResponse response = RawCommand("download:" + length.ToString("x8"));
            if (response.Result == FastbootState.Fail)
                return response;

            using var sha256 = SHA256.Create();
            byte[] buffer = new byte[OnceSendDataSize];
            long bytesRead = 0;
            while (bytesRead < length)
            {
                int toRead = (int)Math.Min(OnceSendDataSize, length - bytesRead);
                int readSize = stream.Read(buffer, 0, toRead);
                if (readSize <= 0) break;

                sha256.TransformBlock(buffer, 0, readSize, null, 0);
                UsbDevice.Write(buffer, readSize);
                bytesRead += readSize;
                if (onEvent)
                    DataTransferProgressChanged?.Invoke(this, (bytesRead, length));
            }
            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

            var res = HandleResponse();
            if (res.Result == FastbootState.Success && sha256.Hash != null)
            {
                res.Hash = BitConverter.ToString(sha256.Hash).Replace("-", "").ToLower();
            }
            return res;
        }

        /// <summary>
        /// 执行 stage 命令，将本地数据发送到设备内存（用于后续 boot 或 flash 指令，视设备而定）
        /// </summary>
        public FastbootResponse Stage(byte[] data)
        {
            CurrentStepChanged?.Invoke(this, "Staging data...");
            FastbootResponse downloadRes = DownloadData(data);
            if (downloadRes.Result != FastbootState.Success) return downloadRes;

            return RawCommand("stage");
        }

        /// <summary>
        /// 执行 stage 命令，将本地数据发送到设备内存
        /// </summary>
        public FastbootResponse Stage(Stream stream, long length)
        {
            CurrentStepChanged?.Invoke(this, "Staging data from stream...");
            FastbootResponse downloadRes = DownloadData(stream, length);
            if (downloadRes.Result != FastbootState.Success) return downloadRes;

            return RawCommand("stage");
        }

        /// <summary>
        /// 从设备上传数据 (对应协议中的上传)
        /// </summary>
        public FastbootResponse UploadData(string command, Stream output)
        {
            FastbootResponse response = RawCommand(command);
            if (response.Result != FastbootState.Data)
                throw new Exception("Unexpected response for upload: " + response.Result);

            long size = response.DataSize;
            long bytesDownloaded = 0;
            while (bytesDownloaded < size)
            {
                int toRead = (int)Math.Min(OnceSendDataSize, size - bytesDownloaded);
                byte[] data = UsbDevice.Read(toRead);
                if (data == null || data.Length == 0) throw new Exception("Unexpected EOF from USB.");
                output.Write(data, 0, data.Length);
                bytesDownloaded += data.Length;
                DataTransferProgressChanged?.Invoke(this, (bytesDownloaded, size));
            }

            return HandleResponse();
        }

        /// <summary>
        /// 快照更新操作 (Virtual A/B)
        /// </summary>
        public FastbootResponse SnapshotUpdate(string action = "cancel")
        {
            if (action != "cancel" && action != "merge")
                throw new ArgumentException("SnapshotUpdate action must be 'cancel' or 'merge'");
            return RawCommand("snapshot-update:" + action);
        }

        /// <summary>
        /// 校验 Product Info (android-info.txt)
        /// </summary>
        public bool ValidateProductInfo(string content, out string? error)
        {
            var parser = new ProductInfoParser(this);
            return parser.Validate(content, out error);
        }

        /// <summary>
        /// 从分区回读并抓取数据 (fetch)
        /// </summary>
        public FastbootResponse Fetch(string partition, string outputPath, long offset = 0, long size = -1)
        {
            string targetPartition = partition;
            if (HasSlot(partition))
            {
                targetPartition = partition + "_" + GetCurrentSlot();
            }

            string cmd = "fetch:" + targetPartition;
            if (offset > 0 || size > 0)
            {
                cmd += ":" + offset.ToString("x8");
                if (size > 0)
                {
                    cmd += ":" + size.ToString("x8");
                }
            }

            using var fs = File.Create(outputPath);
            return UploadData(cmd, fs);
        }

        /// <summary>
        /// 执行 legacy upload 指令，回传设备镜像或日志 (如 upload:last_kmsg)
        /// </summary>
        public FastbootResponse Upload(string filename, string outputPath)
        {
            using var fs = File.Create(outputPath);
            return UploadData("upload:" + filename, fs);
        }

        /// <summary>
        /// 下载并抓取已分阶段的数据 (staged data)
        /// </summary>
        public void GetStaged(string outputPath)
        {
            using var fs = File.Create(outputPath);
            UploadData("get_staged", fs);
        }

        /// <summary>
        /// 打印标准设备信息（bootloader版本、基带版本、序列号等）
        /// </summary>
        public void DumpInfo()
        {
            CurrentStepChanged?.Invoke(this, "--------------------------------------------");
            try { CurrentStepChanged?.Invoke(this, "Bootloader Version...: " + GetVar("version-bootloader")); } catch { }
            try { CurrentStepChanged?.Invoke(this, "Baseband Version.....: " + GetVar("version-baseband")); } catch { }
            try { CurrentStepChanged?.Invoke(this, "Serial Number........: " + GetVar("serialno")); } catch { }
            CurrentStepChanged?.Invoke(this, "--------------------------------------------");
        }

        /// <summary>
        /// 刷入 ZIP 镜像包 (对应 fastboot update)
        /// </summary>
        public void FlashZip(string zipPath, bool skipValidation = false, bool wipe = false)
        {
            DumpInfo();
            CurrentStepChanged?.Invoke(this, $"Parsing ZIP: {Path.GetFileName(zipPath)}");
            using var archive = ZipFile.OpenRead(zipPath);

            var infoEntry = archive.GetEntry("android-info.txt") ?? archive.GetEntry("android-product.txt");
            if (infoEntry != null && !skipValidation)
            {
                using var reader = new StreamReader(infoEntry.Open());
                string content = reader.ReadToEnd();
                if (!ValidateProductInfo(content, out string? error))
                {
                    throw new Exception("Product Info Validation Failed: " + error);
                }
                CurrentStepChanged?.Invoke(this, "Product Info validated successfully.");
            }

            foreach (var entry in archive.Entries)
            {
                if (!entry.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)) continue;

                string part = Path.GetFileNameWithoutExtension(entry.Name);
                CurrentStepChanged?.Invoke(this, $"Processing {part} from ZIP...");
                
                string tempFile = Path.Combine(Path.GetTempPath(), part + "_" + Guid.NewGuid().ToString("N") + ".img");
                entry.ExtractToFile(tempFile, true);
                
                try 
                {
                    FlashImage(part, tempFile);
                    
                    var sigEntry = archive.GetEntry(part + ".sig");
                    if (sigEntry != null)
                    {
                        using var sigStream = sigEntry.Open();
                        using var ms = new MemoryStream();
                        sigStream.CopyTo(ms);
                        Signature(ms.ToArray());
                    }
                }
                finally
                {
                    if (File.Exists(tempFile)) File.Delete(tempFile);
                }
            }

            if (wipe) WipeUserData();
        }

        /// <summary>
        /// 刷入非稀疏镜像(Already Error check)
        /// </summary>
        public FastbootResponse FlashUnsparseImage(string partition, Stream stream, long length)
        {
            CurrentStepChanged?.Invoke(this, $"Sending {partition}");
            DownloadData(stream, length).ThrowIfError();
            CurrentStepChanged?.Invoke(this, $"Flashing {partition}");
            return RawCommand("flash:" + partition).ThrowIfError();
        }

        /// <summary>
        /// 刷入稀疏镜像(Already Error check)
        /// </summary>
        public FastbootResponse FlashSparseImage(string partition, string filePath)
        {
            int count = 1;
            FastbootResponse response = new FastbootResponse();
            int max_download_size = SparseMaxDownloadSize;
            int.TryParse(GetVar("max-download-size").TrimStart("0x"),
                System.Globalization.NumberStyles.HexNumber, null, out max_download_size);
            SparseFile sfile = SparseFile.FromImageFile(filePath);
            var parts = sfile.Resparse(max_download_size);
            foreach (var item in parts)
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

        /// <summary>
        /// 判断分区是否支持插槽（带缓存）
        /// </summary>
        public bool HasSlot(string partition)
        {
            if (_hasSlotCache.TryGetValue(partition, out bool cached)) return cached;
            try
            {
                string res = GetVar("has-slot:" + partition);
                bool has = res == "yes" || res == "1";
                _hasSlotCache[partition] = has;
                return has;
            }
            catch
            {
                return false;
            }
        }

        public FastbootResponse SetActiveSlot(string slot)
        {
            var res = RawCommand("set_active:" + slot);
            if (res.Result == FastbootState.Success)
            {
                _varCache.Remove("current-slot");
            }
            return res;
        }
        
        public FastbootResponse ErasePartition(string partition)
        {
            if (HasSlot(partition))
            {
                partition += "_" + GetCurrentSlot();
            }
            return RawCommand("erase:" + partition);
        }

        /// <summary>
        /// 智能刷入镜像 (根据魔数自动判断是否为稀疏镜像，并自动处理 A/B 插槽)
        /// </summary>
        public void FlashImage(string partition, string filePath)
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException(filePath);

            string targetPartition = partition;
            if (HasSlot(partition))
            {
                targetPartition = partition + "_" + GetCurrentSlot();
            }

            try
            {
                var header = SparseFile.PeekHeader(filePath);
                if (header.Magic == SparseFormat.SparseHeaderMagic)
                {
                    FlashSparseImage(targetPartition, filePath);
                }
                else
                {
                    using var fs = File.OpenRead(filePath);
                    FlashUnsparseImage(targetPartition, fs, fs.Length);
                }
            }
            catch
            {
                using var fs = File.OpenRead(filePath);
                FlashUnsparseImage(targetPartition, fs, fs.Length);
            }
        }

        /// <summary>
        /// 从流刷入镜像
        /// </summary>
        public void FlashImage(string partition, Stream stream)
        {
            string targetPartition = partition;
            if (HasSlot(partition))
            {
                targetPartition = partition + "_" + GetCurrentSlot();
            }

            long oldPos = -1;
            if (stream.CanSeek) oldPos = stream.Position;
            
            try
            {
                byte[] headerBytes = new byte[SparseFormat.SparseHeaderSize];
                stream.ReadExactly(headerBytes, 0, headerBytes.Length);
                if (stream.CanSeek) stream.Position = oldPos;
                else throw new NotSupportedException("Cannot seek stream to check sparse header, use file instead.");

                var header = SparseHeader.FromBytes(headerBytes);
                if (header.Magic == SparseFormat.SparseHeaderMagic)
                {
                    string tmp = Path.GetTempFileName();
                    try
                    {
                        using (var fs = File.Create(tmp)) stream.CopyTo(fs);
                        FlashSparseImage(targetPartition, tmp);
                    }
                    finally
                    {
                        if (File.Exists(tmp)) File.Delete(tmp);
                    }
                }
                else
                {
                    FlashUnsparseImage(targetPartition, stream, stream.Length);
                }
            }
            catch
            {
                if (stream.CanSeek && oldPos != -1) stream.Position = oldPos;
                FlashUnsparseImage(targetPartition, stream, stream.Length);
            }
        }

        /// <summary>
        /// 执行 OEM 命令
        /// </summary>
        public FastbootResponse OemCommand(string oemCmd) => RawCommand("oem " + oemCmd);

        /// <summary>
        /// 执行 Flashing 子命令 (现代解锁命令)
        /// </summary>
        public FastbootResponse FlashingCommand(string subCmd) => RawCommand("flashing " + subCmd);

        public FastbootResponse FlashingUnlock() => FlashingCommand("unlock");
        public FastbootResponse FlashingLock() => FlashingCommand("lock");
        public FastbootResponse FlashingUnlockCritical() => FlashingCommand("unlock_critical");
        public FastbootResponse FlashingLockCritical() => FlashingCommand("lock_critical");
        public bool FlashingGetUnlockAbility()
        {
            var res = FlashingCommand("get_unlock_ability");
            return res.Response.Trim() == "1";
        }

        /// <summary>
        /// 继续启动过程
        /// </summary>
        public FastbootResponse Continue() => RawCommand("continue");

        /// <summary>
        /// 格式化分区
        /// </summary>
        public FastbootResponse FormatPartition(string partition) => RawCommand("format:" + partition);

        /// <summary>
        /// 判断分区是否为逻辑分区
        /// </summary>
        public bool IsLogical(string partition)
        {
            try { return GetVar("is-logical:" + partition) == "yes"; } catch { return false; }
        }

        /// <summary>
        /// 获取分区的存储空间大小
        /// </summary>
        public string GetPartitionSize(string partition)
        {
            try { return GetVar("partition-size:" + partition); } catch { return ""; }
        }

        /// <summary>
        /// 获取分区的存储系统类型
        /// </summary>
        public string GetPartitionType(string partition)
        {
            try { return GetVar("partition-type:" + partition); } catch { return ""; }
        }

        /// <summary>
        /// 本地构建文件系统镜像并刷入（模拟 fastboot format 命令）
        /// </summary>
        public void FormatPartitionLocal(string partition, string fsType = "ext4", long size = 0)
        {
            if (size <= 0)
            {
                var res = GetVar("partition-size:" + partition);
                if (!string.IsNullOrEmpty(res))
                {
                    if (res.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        size = Convert.ToInt64(res, 16);
                    else
                        size = Convert.ToInt64(res);
                }
            }
            if (size <= 0) size = 1024 * 1024 * 32;

            string tmpFile = Path.GetTempFileName();
            try
            {
                if (fsType == "ext4") FileSystemUtil.CreateEmptyExt4(tmpFile, size);
                else if (fsType == "f2fs") FileSystemUtil.CreateEmptyF2fs(tmpFile, size);
                else throw new NotSupportedException("fs type not supported: " + fsType);

                FlashImage(partition, tmpFile);
            }
            finally
            {
                if (File.Exists(tmpFile)) File.Delete(tmpFile);
            }
        }

        /// <summary>
        /// 创建逻辑分区
        /// </summary>
        public FastbootResponse CreateLogicalPartition(string partition, long size)
            => RawCommand($"create-logical-partition:{partition}:{size}");

        /// <summary>
        /// 删除逻辑分区
        /// </summary>
        public FastbootResponse DeleteLogicalPartition(string partition)
            => RawCommand($"delete-logical-partition:{partition}");

        /// <summary>
        /// 调整逻辑分区大小
        /// </summary>
        public FastbootResponse ResizeLogicalPartition(string partition, long size)
            => RawCommand($"resize-logical-partition:{partition}:{size}");

        /// <summary>
        /// 发送并引导内核 (不写入 Flash)
        /// </summary>
        public FastbootResponse Boot(byte[] data)
        {
            DownloadData(data).ThrowIfError();
            return RawCommand("boot");
        }

        /// <summary>
        /// 发送并在内存中引导内核镜像文件
        /// </summary>
        public FastbootResponse Boot(string filePath)
        {
            using var fs = File.OpenRead(filePath);
            DownloadData(fs, fs.Length).ThrowIfError();
            return RawCommand("boot");
        }

        /// <summary>
        /// 混合打包并引导内核 (V0)
        /// </summary>
        public FastbootResponse Boot(string kernelPath, string? ramdiskPath = null, string? secondPath = null, string? cmdline = null, uint base_addr = 0x10000000, uint page_size = 2048)
        {
            byte[] kernel = File.ReadAllBytes(kernelPath);
            byte[]? ramdisk = ramdiskPath != null ? File.ReadAllBytes(ramdiskPath) : null;
            byte[]? second = secondPath != null ? File.ReadAllBytes(secondPath) : null;
            byte[] bootImg = CreateBootImage(kernel, ramdisk, second, cmdline, null, base_addr, page_size);
            return Boot(bootImg);
        }

        /// <summary>
        /// 刷入由 kernel 和 ramdisk 混合生成的原始镜像
        /// </summary>
        public FastbootResponse FlashRaw(string partition, byte[] kernel, byte[]? ramdisk = null, byte[]? second = null, string? cmdline = null, string? name = null, uint base_addr = 0x10000000, uint page_size = 2048)
        {
            byte[] bootImg = CreateBootImage(kernel, ramdisk, second, cmdline, name, base_addr, page_size);
            DownloadData(bootImg).ThrowIfError();
            return RawCommand("flash:" + partition);
        }

        /// <summary>
        /// 从文件混合生成并刷入原始镜像
        /// </summary>
        public FastbootResponse FlashRaw(string partition, string kernelPath, string? ramdiskPath = null, string? secondPath = null, string? cmdline = null, string? name = null, uint base_addr = 0x10000000, uint page_size = 2048)
        {
            byte[] kernel = File.ReadAllBytes(kernelPath);
            byte[]? ramdisk = ramdiskPath != null ? File.ReadAllBytes(ramdiskPath) : null;
            byte[]? second = secondPath != null ? File.ReadAllBytes(secondPath) : null;
            return FlashRaw(partition, kernel, ramdisk, second, cmdline, name, base_addr, page_size);
        }

        /// <summary>
        /// 生成 BootImage 数据 (V0 结构)
        /// </summary>
        public byte[] CreateBootImage(byte[] kernel, byte[]? ramdisk, byte[]? second, string? cmdline, string? name, uint base_addr, uint page_size)
        {
            BootImageHeaderV0 header = BootImageHeaderV0.Create();
            header.KernelSize = (uint)kernel.Length;
            header.KernelAddr = base_addr + 0x00008000;
            header.RamdiskSize = (uint)(ramdisk?.Length ?? 0);
            header.RamdiskAddr = base_addr + 0x01000000;
            header.SecondSize = (uint)(second?.Length ?? 0);
            header.SecondAddr = base_addr + 0x00F00000;
            header.TagsAddr = base_addr + 0x00000100;
            header.PageSize = page_size;

            if (!string.IsNullOrEmpty(cmdline))
            {
                byte[] cmdBytes = Encoding.ASCII.GetBytes(cmdline);
                Array.Copy(cmdBytes, header.Cmdline, Math.Min(cmdBytes.Length, 512));
            }

            if (!string.IsNullOrEmpty(name))
            {
                byte[] nameBytes = Encoding.ASCII.GetBytes(name);
                Array.Copy(nameBytes, header.Name, Math.Min(nameBytes.Length, 16));
            }

            int headerSize = Marshal.SizeOf<BootImageHeaderV0>();
            int headerPages = (headerSize + (int)page_size - 1) / (int)page_size;
            int kernelPages = (kernel.Length + (int)page_size - 1) / (int)page_size;
            int ramdiskPages = ((ramdisk?.Length ?? 0) + (int)page_size - 1) / (int)page_size;
            int secondPages = ((second?.Length ?? 0) + (int)page_size - 1) / (int)page_size;

            int totalSize = (headerPages + kernelPages + ramdiskPages + secondPages) * (int)page_size;
            byte[] buffer = new byte[totalSize];

            byte[] headerBytes = DataHelper.Struct2Bytes(header);
            Array.Copy(headerBytes, 0, buffer, 0, headerBytes.Length);
            Array.Copy(kernel, 0, buffer, headerPages * page_size, kernel.Length);
            if (ramdisk != null)
            {
                Array.Copy(ramdisk, 0, buffer, (headerPages + kernelPages) * page_size, ramdisk.Length);
            }
            if (second != null)
            {
                Array.Copy(second, 0, buffer, (headerPages + kernelPages + ramdiskPages) * page_size, second.Length);
            }

            return buffer;
        }

        /// <summary>
        /// 生成 BootImage V2 数据 (含 DTB)
        /// </summary>
        public byte[] CreateBootImage2(byte[] kernel, byte[]? ramdisk, byte[]? second, byte[]? dtb, string? cmdline, string? name, uint base_addr, uint page_size)
        {
            BootImageHeaderV2 header = BootImageHeaderV2.Create();
            header.KernelSize = (uint)kernel.Length;
            header.KernelAddr = base_addr + 0x00008000;
            header.RamdiskSize = (uint)(ramdisk?.Length ?? 0);
            header.RamdiskAddr = base_addr + 0x01000000;
            header.SecondSize = (uint)(second?.Length ?? 0);
            header.SecondAddr = base_addr + 0x00F00000;
            header.TagsAddr = base_addr + 0x00000100;
            header.DtbSize = (uint)(dtb?.Length ?? 0);
            header.DtbAddr = (ulong)base_addr + 0x01100000;
            header.PageSize = page_size;
            header.HeaderSize = (uint)Marshal.SizeOf<BootImageHeaderV2>();

            if (!string.IsNullOrEmpty(cmdline))
            {
                byte[] cmdBytes = Encoding.ASCII.GetBytes(cmdline);
                Array.Copy(cmdBytes, header.Cmdline, Math.Min(cmdBytes.Length, 512));
            }

            if (!string.IsNullOrEmpty(name))
            {
                byte[] nameBytes = Encoding.ASCII.GetBytes(name);
                Array.Copy(nameBytes, header.Name, Math.Min(nameBytes.Length, 16));
            }

            int headerPages = ((int)header.HeaderSize + (int)page_size - 1) / (int)page_size;
            int kernelPages = (kernel.Length + (int)page_size - 1) / (int)page_size;
            int ramdiskPages = ((ramdisk?.Length ?? 0) + (int)page_size - 1) / (int)page_size;
            int secondPages = ((second?.Length ?? 0) + (int)page_size - 1) / (int)page_size;
            int dtbPages = ((dtb?.Length ?? 0) + (int)page_size - 1) / (int)page_size;

            int totalSize = (headerPages + kernelPages + ramdiskPages + secondPages + dtbPages) * (int)page_size;
            byte[] buffer = new byte[totalSize];

            byte[] headerBytes = DataHelper.Struct2Bytes(header);
            Array.Copy(headerBytes, 0, buffer, 0, headerBytes.Length);
            Array.Copy(kernel, 0, buffer, headerPages * page_size, kernel.Length);
            if (ramdisk != null) Array.Copy(ramdisk, 0, buffer, (headerPages + kernelPages) * page_size, ramdisk.Length);
            if (second != null) Array.Copy(second, 0, buffer, (headerPages + kernelPages + ramdiskPages) * page_size, second.Length);
            if (dtb != null) Array.Copy(dtb, 0, buffer, (headerPages + kernelPages + ramdiskPages + secondPages) * page_size, dtb.Length);

            return buffer;
        }

        /// <summary>
        /// 生成 BootImage V3 数据
        /// </summary>
        public byte[] CreateBootImage3(byte[] kernel, byte[]? ramdisk, string? cmdline, uint os_version)
        {
            BootImageHeaderV3 header = BootImageHeaderV3.Create();
            header.KernelSize = (uint)kernel.Length;
            header.RamdiskSize = (uint)(ramdisk?.Length ?? 0);
            header.OsVersion = os_version;
            header.HeaderSize = 4096;
            header.HeaderVersion = 3;

            if (!string.IsNullOrEmpty(cmdline))
            {
                byte[] cmdBytes = Encoding.ASCII.GetBytes(cmdline);
                Array.Copy(cmdBytes, header.Cmdline, Math.Min(cmdBytes.Length, 1536));
            }

            const int page_size = 4096;
            int headerPages = (int)(header.HeaderSize + page_size - 1) / page_size;
            int kernelPages = (kernel.Length + page_size - 1) / page_size;
            int ramdiskPages = ((ramdisk?.Length ?? 0) + page_size - 1) / page_size;

            int totalSize = (headerPages + kernelPages + ramdiskPages) * page_size;
            byte[] buffer = new byte[totalSize];

            byte[] headerBytes = DataHelper.Struct2Bytes(header);
            Array.Copy(headerBytes, 0, buffer, 0, headerBytes.Length);
            Array.Copy(kernel, 0, buffer, headerPages * page_size, kernel.Length);
            if (ramdisk != null)
                Array.Copy(ramdisk, 0, buffer, (headerPages + kernelPages) * page_size, ramdisk.Length);

            return buffer;
        }

        /// <summary>
        /// 生成 BootImage V4 数据 (不含签名部分)
        /// </summary>
        public byte[] CreateBootImage4(byte[] kernel, byte[]? ramdisk, string? cmdline, uint os_version)
        {
            BootImageHeaderV4 header = BootImageHeaderV4.Create();
            header.KernelSize = (uint)kernel.Length;
            header.RamdiskSize = (uint)(ramdisk?.Length ?? 0);
            header.OsVersion = os_version;
            header.HeaderSize = 4096;
            header.HeaderVersion = 4;

            if (!string.IsNullOrEmpty(cmdline))
            {
                byte[] cmdBytes = Encoding.ASCII.GetBytes(cmdline);
                Array.Copy(cmdBytes, header.Cmdline, Math.Min(cmdBytes.Length, 1536));
            }

            const int page_size = 4096;
            int headerPages = (int)(header.HeaderSize + page_size - 1) / page_size;
            int kernelPages = (kernel.Length + page_size - 1) / page_size;
            int ramdiskPages = ((ramdisk?.Length ?? 0) + page_size - 1) / page_size;

            int totalSize = (headerPages + kernelPages + ramdiskPages) * page_size;
            byte[] buffer = new byte[totalSize];

            byte[] headerBytes = DataHelper.Struct2Bytes(header);
            Array.Copy(headerBytes, 0, buffer, 0, headerBytes.Length);
            Array.Copy(kernel, 0, buffer, headerPages * page_size, kernel.Length);
            if (ramdisk != null)
                Array.Copy(ramdisk, 0, buffer, (headerPages + kernelPages) * page_size, ramdisk.Length);

            return buffer;
        }

        /// <summary>
        /// 发送签名文件
        /// </summary>
        public FastbootResponse Signature(byte[] sigData)
        {
            DownloadData(sigData).ThrowIfError();
            return RawCommand("signature");
        }

        /// <summary>
        /// 校验 android-info.txt 中的需求
        /// </summary>
        public bool VerifyRequirements(string infoText)
        {
            var parser = new ProductInfoParser(this);
            if (!parser.Validate(infoText, out string? error))
            {
                throw new Exception(error);
            }
            return true;
        }

        /// <summary>
        /// 执行 FlashAll (在指定目录下寻找并刷入基础分区)
        /// </summary>
        public void FlashAll(string productOutDir, bool wipe = false)
        {
            string infoPath = Path.Combine(productOutDir, "android-info.txt");
            if (File.Exists(infoPath))
            {
                VerifyRequirements(File.ReadAllText(infoPath));
            }

            var imageFiles = Directory.GetFiles(productOutDir, "*.img")
                .OrderBy(f => {
                    string part = Path.GetFileNameWithoutExtension(f);
                    int index = Array.IndexOf(PartitionPriority, part.ToLower());
                    return index == -1 ? int.MaxValue : index;
                }).ToList();

            foreach (var filePath in imageFiles)
            {
                string part = Path.GetFileNameWithoutExtension(filePath);
                FlashImage(part, filePath);

                string sigPath = Path.Combine(productOutDir, part + ".sig");
                if (File.Exists(sigPath))
                {
                    Signature(File.ReadAllBytes(sigPath));
                }
            }

            if (wipe)
            {
                WipeUserData();
            }
        }

        /// <summary>
        /// 清除用户数据和缓存
        /// </summary>
        public void WipeUserData()
        {
            try { FormatPartition("userdata"); } catch { }
            try { FormatPartition("cache"); } catch { }
        }
    }
}
