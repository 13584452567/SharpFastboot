using LibSparseSharp;
using SharpFastboot.DataModel;
using SharpFastboot.Usb;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using LibLpSharp;

namespace SharpFastboot
{
    public class FastbootUtil
    {
        public IFastbootTransport Transport { get; private set; }
        private Dictionary<string, string> _varCache = new Dictionary<string, string>();
        private Dictionary<string, bool> _hasSlotCache = new Dictionary<string, bool>();
        private HashSet<string>? _logicalPartitionsFromMetadata = null;

        public FastbootUtil(IFastbootTransport transport) => Transport = transport;
        public static int ReadTimeoutSeconds = 30;
        public static int OnceSendDataSize = 1024 * 1024;
        public static int SparseMaxDownloadSize = 256 * 1024 * 1024;

        private static readonly string[] PartitionPriority = {
            "preloader", "bootloader", "radio", "dram", "md1img", "xbl", "abl", "keystore", // SOC critical
            "boot", "dtbo", "init_boot", "vendor_boot", "pvmfw",
            "vbmeta", "vbmeta_system", "vbmeta_vendor", "vbmeta_custom",
            "recovery", "system", "vendor", "product", "system_ext", "odm", "vendor_dlkm", "odm_dlkm", "system_dlkm"
        };

        public event EventHandler<FastbootReceivedFromDeviceEventArgs>? ReceivedFromDevice;
        public event EventHandler<(long, long)>? DataTransferProgressChanged;
        public event EventHandler<string>? CurrentStepChanged;

        public void NotifyCurrentStep(string step) => CurrentStepChanged?.Invoke(this, step);
        public void NotifyProgress(long current, long total) => DataTransferProgressChanged?.Invoke(this, (current, total));
        public void NotifyReceived(FastbootState state, string? info = null, string? text = null) 
            => ReceivedFromDevice?.Invoke(this, new FastbootReceivedFromDeviceEventArgs(state, info, text));

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
                    data = Transport.Read(256);
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
                if (Transport.Write(cmdBytes, cmdBytes.Length) != cmdBytes.Length)
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

        private void LoadLogicalPartitionsFromMetadata(string metadataPath)
        {
            if (!File.Exists(metadataPath)) return;
            try
            {
                var metadata = MetadataReader.ReadFromImageFile(metadataPath);
                _logicalPartitionsFromMetadata = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in metadata.Partitions)
                {
                    _logicalPartitionsFromMetadata.Add(p.GetName());
                }
            }
            catch (Exception ex)
            {
                // Silently fails, as this is just an optimization
                System.Diagnostics.Debug.WriteLine($"Failed to load logical partitions from metadata: {ex.Message}");
            }
        }

        private bool IsLogicalOptimized(string partition)
        {
            if (_logicalPartitionsFromMetadata != null)
            {
                // Check direct name
                if (_logicalPartitionsFromMetadata.Contains(partition)) return true;

                // Check without suffixes if current partition has one
                string withoutSuffix = partition;
                if (partition.EndsWith("_a") || partition.EndsWith("_b"))
                    withoutSuffix = partition.Substring(0, partition.Length - 2);

                if (_logicalPartitionsFromMetadata.Contains(withoutSuffix)) return true;
                
                // If it's still not found, we assume it's NOT logical based on the metadata we loaded.
                // However, some partitions might have been added dynamic manually? Unlikely.
                return false;
            }
            // Fallback to GetVar
            return IsLogical(partition);
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
            Transport.Write(data, data.Length);
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
                Transport.Write(buffer, readSize);
                bytesRead += readSize;
                if (onEvent)
                    NotifyProgress(bytesRead, length);
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
            NotifyCurrentStep("Staging data...");
            FastbootResponse downloadRes = DownloadData(data);
            if (downloadRes.Result != FastbootState.Success) return downloadRes;

            return RawCommand("stage");
        }

        /// <summary>
        /// 执行 stage 命令，将本地数据发送到设备内存
        /// </summary>
        public FastbootResponse Stage(Stream stream, long length)
        {
            NotifyCurrentStep("Staging data from stream...");
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
                byte[] data = Transport.Read(toRead);
                if (data == null || data.Length == 0) throw new Exception("Unexpected EOF from USB.");
                output.Write(data, 0, data.Length);
                bytesDownloaded += data.Length;
                NotifyProgress(bytesDownloaded, size);
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
            var res = RawCommand("snapshot-update:" + action);
            if (res.Response.Contains("reboot fastboot", StringComparison.OrdinalIgnoreCase))
            {
                NotifyCurrentStep("Device requested reboot to fastbootd to finish snapshot action...");
                Reboot("fastboot");
            }
            return res;
        }

        /// <summary>
        /// 如果存在活跃的虚拟 A/B 快照，则尝试取消它
        /// </summary>
        public void CancelSnapshotIfNeeded()
        {
            try
            {
                string status = GetVar("snapshot-update-status");
                if (!string.IsNullOrEmpty(status) && status != "none")
                {
                    SnapshotUpdate("cancel");
                }
            }
            catch { }
        }

        /// <summary>
        /// 确保设备处于 fastbootd (userspace) 模式，如果不是则自动重启
        /// </summary>
        public void EnsureUserspace()
        {
            if (!IsUserspace())
            {
                NotifyCurrentStep("Operation requires fastbootd, rebooting...");
                Reboot("fastboot").ThrowIfError();
                
                System.Threading.Thread.Sleep(2000);

                if (Transport is UsbDevice usbDev)
                {
                    var newUtil = WaitForDevice(UsbManager.GetAllDevices, usbDev.SerialNumber, 30);
                    if (newUtil == null) throw new Exception("Failed to reconnect to device after rebooting to fastbootd.");
                    
                    this.Transport = newUtil.Transport;
                }
                else if (Transport is TcpTransport tcp)
                {
                    string host = tcp.Host;
                    int port = tcp.Port;
                    tcp.Dispose();

                    DateTime start = DateTime.Now;
                    bool connected = false;
                    while ((DateTime.Now - start).TotalSeconds < 60)
                    {
                        try 
                        {
                            Transport = new TcpTransport(host, port);
                            connected = true;
                            break;
                        }
                        catch { System.Threading.Thread.Sleep(1000); }
                    }
                    if (!connected) throw new Exception("Failed to reconnect to TCP device after rebooting to fastbootd.");
                }
                else if (Transport is UdpTransport udp)
                {
                    string host = udp.Host;
                    int port = udp.Port;
                    udp.Dispose();

                    DateTime start = DateTime.Now;
                    bool connected = false;
                    while ((DateTime.Now - start).TotalSeconds < 60)
                    {
                        try 
                        {
                            Transport = new UdpTransport(host, port);
                            connected = true;
                            break;
                        }
                        catch { System.Threading.Thread.Sleep(1000); }
                    }
                    if (!connected) throw new Exception("Failed to reconnect to UDP device after rebooting to fastbootd.");
                }
                else
                {
                    throw new NotSupportedException("Automatic reboot to userspace is only supported for USB, TCP and UDP transports.");
                }
                _varCache.Clear();
            }
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
            NotifyCurrentStep("--------------------------------------------");
            try { NotifyCurrentStep("Bootloader Version...: " + GetVar("version-bootloader")); } catch { }
            try { NotifyCurrentStep("Baseband Version.....: " + GetVar("version-baseband")); } catch { }
            try { NotifyCurrentStep("Serial Number........: " + GetVar("serialno")); } catch { }
            NotifyCurrentStep("--------------------------------------------");
        }

        /// <summary>
        /// 刷入 ZIP 刷机包 (对应 fastboot update)
        /// </summary>
        public void FlashZip(string zipPath, bool skipValidation = false, bool wipe = false)
        {
            CancelSnapshotIfNeeded();
            DumpInfo();
            
            string tempDir = Path.Combine(Path.GetTempPath(), "SharpFastboot_Zip_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            
            try
            {
                NotifyCurrentStep($"Extracting ZIP: {Path.GetFileName(zipPath)}");
                ZipFile.ExtractToDirectory(zipPath, tempDir);

                // 复用 FlashAll 逻辑，它会自动处理 fastboot-info.txt、优化 super 分区刷入、校验及清空数据
                FlashAll(tempDir, wipe, false, skipValidation);
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// 刷入非稀疏镜像(Already Error check)
        /// </summary>
        public FastbootResponse FlashUnsparseImage(string partition, Stream stream, long length)
        {
            NotifyCurrentStep($"Sending {partition}");
            DownloadData(stream, length).ThrowIfError();
            NotifyCurrentStep($"Flashing {partition}");
            return RawCommand("flash:" + partition).ThrowIfError();
        }

        public long GetMaxDownloadSize()
        {
            var sizeStr = GetVar("max-download-size");
            if (string.IsNullOrEmpty(sizeStr)) return SparseMaxDownloadSize;

            if (sizeStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(sizeStr.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out long res))
                    return res;
            }
            else
            {
                if (long.TryParse(sizeStr, out long res))
                    return res;
            }
            return SparseMaxDownloadSize;
        }

        /// <summary>
        /// 刷入稀疏镜像(Already Error check)
        /// </summary>
        public FastbootResponse FlashSparseImage(string partition, string filePath)
        {
            long maxDownloadSize = GetMaxDownloadSize();
            SparseFile sfile = SparseFile.FromImageFile(filePath);
            return FlashSparseFile(partition, sfile, maxDownloadSize);
        }

        /// <summary>
        /// 刷入稀疏文件对象
        /// </summary>
        public FastbootResponse FlashSparseFile(string partition, SparseFile sfile, long maxDownloadSize)
        {
            bool useCrc = HasCrc();
            int count = 1;
            FastbootResponse response = new FastbootResponse();
            var parts = sfile.Resparse(maxDownloadSize);
            foreach (var item in parts)
            {
                using Stream stream = item.GetExportStream(0, item.Header.TotalBlocks, useCrc);
                NotifyCurrentStep($"Sending {partition}({count} / {parts.Count})" + (useCrc ? " (with CRC)" : ""));
                DownloadData(stream, stream.Length).ThrowIfError();
                NotifyCurrentStep($"Flashing {partition}({count} / {parts.Count})");
                response = RawCommand("flash:" + partition);
                response.ThrowIfError();
                count++;
            }
            return response;
        }

        public string GetCurrentSlot() => GetVar("current-slot");

        /// <summary>
        /// 是否支持 CRC 校验码 (AOSP sparse 协议扩展)
        /// </summary>
        public bool HasCrc()
        {
            try { return GetVar("has-crc") == "yes"; } catch { return false; }
        }

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

        /// <summary>
        /// 检查分区是否存在
        /// </summary>
        public bool PartitionExists(string partition)
        {
            try
            {
                string res = GetPartitionSize(partition);
                return !string.IsNullOrEmpty(res) && res != "0" && res != "0x0";
            }
            catch { return false; }
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
        public void FlashImage(string partition, string filePath, string? slotOverride = null)
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException(filePath);

            string targetPartition = partition;
            if (slotOverride == "all")
            {
                FlashImage(partition, filePath, "a");
                FlashImage(partition, filePath, "b");
                return;
            }

            if (HasSlot(partition))
            {
                targetPartition = partition + "_" + (slotOverride ?? GetCurrentSlot());
            }

            // AOSP Optimal Placement: For logical partitions, zero out existing size
            // ResizeLogicalPartition also handles ensuring userspace (fastbootd)
            if (IsLogicalOptimized(targetPartition))
            {
                try { ResizeLogicalPartition(targetPartition, 0); } catch { }
            }

            long maxDownloadSize = GetMaxDownloadSize();
            try
            {
                var header = SparseFile.PeekHeader(filePath);
                if (header.Magic == SparseFormat.SparseHeaderMagic)
                {
                    FlashSparseImage(targetPartition, filePath);
                }
                else
                {
                    FileInfo fi = new FileInfo(filePath);
                    if (fi.Length > maxDownloadSize)
                    {
                        // 原始镜像过大，自动封装为稀疏镜像分段刷入
                        FlashSparseFile(targetPartition, SparseFile.FromRawFile(filePath), maxDownloadSize);
                    }
                    else
                    {
                        using var fs = File.OpenRead(filePath);
                        FlashUnsparseImage(targetPartition, fs, fs.Length);
                    }
                }
            }
            catch (Exception)
            {
                FileInfo fi = new FileInfo(filePath);
                if (fi.Length > maxDownloadSize)
                {
                    FlashSparseFile(targetPartition, SparseFile.FromRawFile(filePath), maxDownloadSize);
                }
                else
                {
                    using var fs = File.OpenRead(filePath);
                    FlashUnsparseImage(targetPartition, fs, fs.Length);
                }
            }
        }

        public FastbootResponse GsiWipe() => GsiCommand("wipe");
        public FastbootResponse GsiDisable() => GsiCommand("disable");
        public FastbootResponse GsiStatus() => GsiCommand("status");

        /// <summary>
        /// 等待虚拟 A/B 合成完成 (对应 snapshot-update merge --wait)
        /// </summary>
        public void WaitForSnapshotMerge(int timeoutSeconds = 600)
        {
            DateTime start = DateTime.Now;
            while ((DateTime.Now - start).TotalSeconds < timeoutSeconds)
            {
                var res = GetVar("snapshot-update-status");
                if (res == "merging")
                {
                    NotifyCurrentStep("Waiting for snapshot merge...");
                    System.Threading.Thread.Sleep(2000);
                    continue;
                }
                if (res == "none" || res == "completed") return;
                break;
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

            // AOSP Optimal Placement: For logical partitions, zero out existing size
            // ResizeLogicalPartition also handles ensuring userspace (fastbootd)
            if (IsLogicalOptimized(targetPartition))
            {
                try { ResizeLogicalPartition(targetPartition, 0); } catch { }
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
                    // Streaming optimization: Use SparseFile directly from stream
                    using var sfile = SparseFile.FromStream(stream);
                    FlashSparseFile(targetPartition, sfile, GetMaxDownloadSize());
                    return;
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
        {
            EnsureUserspace();
            return RawCommand($"create-logical-partition:{partition}:{size}");
        }

        /// <summary>
        /// 删除逻辑分区
        /// </summary>
        public FastbootResponse DeleteLogicalPartition(string partition)
        {
            EnsureUserspace();
            return RawCommand($"delete-logical-partition:{partition}");
        }

        /// <summary>
        /// 调整逻辑分区大小
        /// </summary>
        public FastbootResponse ResizeLogicalPartition(string partition, long size)
        {
            EnsureUserspace();
            return RawCommand($"resize-logical-partition:{partition}:{size}");
        }

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
        /// 混合打包并引导内核
        /// </summary>
        public FastbootResponse Boot(string kernelPath, string? ramdiskPath = null, string? secondPath = null, string? dtbPath = null, string? cmdline = null, uint header_version = 0, uint base_addr = 0x10000000, uint page_size = 2048)
        {
            byte[] kernel = File.ReadAllBytes(kernelPath);
            byte[]? ramdisk = ramdiskPath != null ? File.ReadAllBytes(ramdiskPath) : null;
            byte[]? second = secondPath != null ? File.ReadAllBytes(secondPath) : null;
            byte[]? dtb = dtbPath != null ? File.ReadAllBytes(dtbPath) : null;
            
            byte[] bootImg = CreateBootImageVersioned(kernel, ramdisk, second, dtb, cmdline, null, header_version, base_addr, page_size);
            return Boot(bootImg);
        }

        /// <summary>
        /// 刷入由各组件混合生成的原始镜像 (对应 AOSP flash:raw)
        /// </summary>
        public FastbootResponse FlashRaw(string partition, string kernelPath, string? ramdiskPath = null, string? secondPath = null, string? dtbPath = null, string? cmdline = null, uint header_version = 0, uint base_addr = 0x10000000, uint page_size = 2048)
        {
            byte[] kernel = File.ReadAllBytes(kernelPath);
            byte[]? ramdisk = ramdiskPath != null ? File.ReadAllBytes(ramdiskPath) : null;
            byte[]? second = secondPath != null ? File.ReadAllBytes(secondPath) : null;
            byte[]? dtb = dtbPath != null ? File.ReadAllBytes(dtbPath) : null;

            byte[] bootImg = CreateBootImageVersioned(kernel, ramdisk, second, dtb, cmdline, null, header_version, base_addr, page_size);
            DownloadData(bootImg).ThrowIfError();
            return RawCommand("flash:" + partition);
        }

        private byte[] CreateBootImageVersioned(byte[] kernel, byte[]? ramdisk, byte[]? second, byte[]? dtb, string? cmdline, string? name, uint version, uint base_addr, uint page_size)
        {
            switch (version)
            {
                case 0: return CreateBootImage(kernel, ramdisk, second, cmdline, name, base_addr, page_size);
                case 1: return CreateBootImage1(kernel, ramdisk, second, cmdline, name, base_addr, page_size);
                case 2: return CreateBootImage2(kernel, ramdisk, second, dtb, cmdline, name, base_addr, page_size);
                case 3: return CreateBootImage3(kernel, ramdisk, cmdline, 0); // OS version 0 as default
                case 4: return CreateBootImage4(kernel, ramdisk, cmdline, 0);
                case 5: return CreateBootImage5(kernel, ramdisk, cmdline, 0);
                case 6: return CreateBootImage6(kernel, ramdisk, cmdline, 0);
                default: throw new NotSupportedException($"Boot image header version {version} is not supported for dynamic packaging.");
            }
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
        /// 生成 BootImage V1 数据 (含 Header Size)
        /// </summary>
        public byte[] CreateBootImage1(byte[] kernel, byte[]? ramdisk, byte[]? second, string? cmdline, string? name, uint base_addr, uint page_size)
        {
            BootImageHeaderV1 header = BootImageHeaderV1.Create();
            header.KernelSize = (uint)kernel.Length;
            header.KernelAddr = base_addr + 0x00008000;
            header.RamdiskSize = (uint)(ramdisk?.Length ?? 0);
            header.RamdiskAddr = base_addr + 0x01000000;
            header.SecondSize = (uint)(second?.Length ?? 0);
            header.SecondAddr = base_addr + 0x00F00000;
            header.TagsAddr = base_addr + 0x00000100;
            header.PageSize = page_size;
            header.HeaderSize = (uint)Marshal.SizeOf<BootImageHeaderV1>();

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

            int totalSize = (headerPages + kernelPages + ramdiskPages + secondPages) * (int)page_size;
            byte[] buffer = new byte[totalSize];

            byte[] headerBytes = DataHelper.Struct2Bytes(header);
            Array.Copy(headerBytes, 0, buffer, 0, headerBytes.Length);
            Array.Copy(kernel, 0, buffer, headerPages * page_size, kernel.Length);
            if (ramdisk != null) Array.Copy(ramdisk, 0, buffer, (headerPages + kernelPages) * page_size, ramdisk.Length);
            if (second != null) Array.Copy(second, 0, buffer, (headerPages + kernelPages + ramdiskPages) * page_size, second.Length);

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
        /// 生成 BootImage V4 数据 (含签名部分)
        /// </summary>
        public byte[] CreateBootImage4(byte[] kernel, byte[]? ramdisk, string? cmdline, uint os_version, byte[]? signature = null)
        {
            BootImageHeaderV4 header = BootImageHeaderV4.Create();
            header.KernelSize = (uint)kernel.Length;
            header.RamdiskSize = (uint)(ramdisk?.Length ?? 0);
            header.OsVersion = os_version;
            header.HeaderSize = 4096;
            header.HeaderVersion = 4;
            header.SignatureSize = (uint)(signature?.Length ?? 0);

            if (!string.IsNullOrEmpty(cmdline))
            {
                byte[] cmdBytes = Encoding.ASCII.GetBytes(cmdline);
                Array.Copy(cmdBytes, header.Cmdline, Math.Min(cmdBytes.Length, 1536));
            }

            const int page_size = 4096;
            int headerPages = (int)(header.HeaderSize + page_size - 1) / page_size;
            int kernelPages = (kernel.Length + page_size - 1) / page_size;
            int ramdiskPages = ((ramdisk?.Length ?? 0) + page_size - 1) / page_size;
            int sigPages = (int)((header.SignatureSize + page_size - 1) / page_size);

            int totalSize = (headerPages + kernelPages + ramdiskPages + sigPages) * page_size;
            byte[] buffer = new byte[totalSize];

            byte[] headerBytes = DataHelper.Struct2Bytes(header);
            Array.Copy(headerBytes, 0, buffer, 0, headerBytes.Length);
            Array.Copy(kernel, 0, buffer, headerPages * page_size, kernel.Length);
            if (ramdisk != null)
                Array.Copy(ramdisk, 0, buffer, (headerPages + kernelPages) * page_size, ramdisk.Length);
            if (signature != null)
                Array.Copy(signature, 0, buffer, (headerPages + kernelPages + ramdiskPages) * page_size, signature.Length);

            return buffer;
        }

        /// <summary>
        /// 生成 BootImage V5 数据 (含 Vendor Bootconfig)
        /// </summary>
        public byte[] CreateBootImage5(byte[] kernel, byte[]? ramdisk, string? cmdline, uint os_version, byte[]? signature = null, byte[]? bootconfig = null)
        {
            BootImageHeaderV5 header = BootImageHeaderV5.Create();
            header.KernelSize = (uint)kernel.Length;
            header.RamdiskSize = (uint)(ramdisk?.Length ?? 0);
            header.OsVersion = os_version;
            header.HeaderSize = 4096;
            header.HeaderVersion = 5;
            header.SignatureSize = (uint)(signature?.Length ?? 0);
            header.VendorBootconfigSize = (uint)(bootconfig?.Length ?? 0);

            if (!string.IsNullOrEmpty(cmdline))
            {
                byte[] cmdBytes = Encoding.ASCII.GetBytes(cmdline);
                Array.Copy(cmdBytes, header.Cmdline, Math.Min(cmdBytes.Length, 1536));
            }

            const int page_size = 4096;
            int headerPages = (int)(header.HeaderSize + page_size - 1) / page_size;
            int kernelPages = (kernel.Length + page_size - 1) / page_size;
            int ramdiskPages = ((ramdisk?.Length ?? 0) + page_size - 1) / page_size;
            int sigPages = (int)((header.SignatureSize + page_size - 1) / page_size);
            int configPages = (int)((header.VendorBootconfigSize + page_size - 1) / page_size);

            int totalSize = (headerPages + kernelPages + ramdiskPages + sigPages + configPages) * page_size;
            byte[] buffer = new byte[totalSize];

            byte[] headerBytes = DataHelper.Struct2Bytes(header);
            Array.Copy(headerBytes, 0, buffer, 0, headerBytes.Length);
            Array.Copy(kernel, 0, buffer, headerPages * page_size, kernel.Length);
            if (ramdisk != null)
                Array.Copy(ramdisk, 0, buffer, (headerPages + kernelPages) * page_size, ramdisk.Length);
            if (signature != null)
                Array.Copy(signature, 0, buffer, (headerPages + kernelPages + ramdiskPages) * page_size, signature.Length);
            if (bootconfig != null)
                Array.Copy(bootconfig, 0, buffer, (headerPages + kernelPages + ramdiskPages + sigPages) * page_size, bootconfig.Length);

            return buffer;
        }

        /// <summary>
        /// 生成 BootImage V6 数据 (含扩展保留区)
        /// </summary>
        public byte[] CreateBootImage6(byte[] kernel, byte[]? ramdisk, string? cmdline, uint os_version, byte[]? signature = null, byte[]? bootconfig = null)
        {
            BootImageHeaderV6 header = BootImageHeaderV6.Create();
            header.KernelSize = (uint)kernel.Length;
            header.RamdiskSize = (uint)(ramdisk?.Length ?? 0);
            header.OsVersion = os_version;
            header.HeaderSize = 4096;
            header.HeaderVersion = 6;
            header.SignatureSize = (uint)(signature?.Length ?? 0);
            header.VendorBootconfigSize = (uint)(bootconfig?.Length ?? 0);

            if (!string.IsNullOrEmpty(cmdline))
            {
                byte[] cmdBytes = Encoding.ASCII.GetBytes(cmdline);
                Array.Copy(cmdBytes, header.Cmdline, Math.Min(cmdBytes.Length, 1536));
            }

            const int page_size = 4096;
            int headerPages = (int)(header.HeaderSize + page_size - 1) / page_size;
            int kernelPages = (kernel.Length + page_size - 1) / page_size;
            int ramdiskPages = ((ramdisk?.Length ?? 0) + page_size - 1) / page_size;
            int sigPages = (int)((header.SignatureSize + page_size - 1) / page_size);
            int configPages = (int)((header.VendorBootconfigSize + page_size - 1) / page_size);

            int totalSize = (headerPages + kernelPages + ramdiskPages + sigPages + configPages) * page_size;
            byte[] buffer = new byte[totalSize];

            byte[] headerBytes = DataHelper.Struct2Bytes(header);
            Array.Copy(headerBytes, 0, buffer, 0, headerBytes.Length);
            Array.Copy(kernel, 0, buffer, headerPages * page_size, kernel.Length);
            if (ramdisk != null)
                Array.Copy(ramdisk, 0, buffer, (headerPages + kernelPages) * page_size, ramdisk.Length);
            if (signature != null)
                Array.Copy(signature, 0, buffer, (headerPages + kernelPages + ramdiskPages) * page_size, signature.Length);
            if (bootconfig != null)
                Array.Copy(bootconfig, 0, buffer, (headerPages + kernelPages + ramdiskPages + sigPages) * page_size, bootconfig.Length);

            return buffer;
        }

        /// <summary>
        /// 生成 Vendor Boot Image V3 数据 (含 DTB)
        /// </summary>
        public byte[] CreateVendorBootImage3(byte[] ramdisk, byte[] dtb, string? cmdline, string? product_name, uint page_size = 4096, uint base_addr = 0x10000000)
        {
            VendorBootImageHeaderV3 header = VendorBootImageHeaderV3.Create();
            header.PageSize = page_size;
            header.KernelAddr = base_addr + 0x00008000;
            header.RamdiskAddr = base_addr + 0x01000000;
            header.TagsAddr = base_addr + 0x00000100;
            header.VendorRamdiskSize = (uint)ramdisk.Length;
            header.DtbSize = (uint)dtb.Length;
            header.DtbAddr = (ulong)base_addr + 0x01100000;
            header.HeaderSize = (uint)Marshal.SizeOf<VendorBootImageHeaderV3>();

            if (!string.IsNullOrEmpty(cmdline))
            {
                byte[] cmdBytes = Encoding.ASCII.GetBytes(cmdline);
                Array.Copy(cmdBytes, header.Cmdline, Math.Min(cmdBytes.Length, 2048));
            }

            if (!string.IsNullOrEmpty(product_name))
            {
                byte[] nameBytes = Encoding.ASCII.GetBytes(product_name);
                Array.Copy(nameBytes, header.Name, Math.Min(nameBytes.Length, 16));
            }

            int headerPages = (int)(header.HeaderSize + page_size - 1) / (int)page_size;
            int ramdiskPages = (ramdisk.Length + (int)page_size - 1) / (int)page_size;
            int dtbPages = (dtb.Length + (int)page_size - 1) / (int)page_size;

            int totalSize = (headerPages + ramdiskPages + dtbPages) * (int)page_size;
            byte[] buffer = new byte[totalSize];

            byte[] headerBytes = DataHelper.Struct2Bytes(header);
            Array.Copy(headerBytes, 0, buffer, 0, headerBytes.Length);
            Array.Copy(ramdisk, 0, buffer, headerPages * page_size, ramdisk.Length);
            Array.Copy(dtb, 0, buffer, (headerPages + ramdiskPages) * page_size, dtb.Length);

            return buffer;
        }

        /// <summary>
        /// 生成 Vendor Boot Image V4 数据 (含 Bootconfig)
        /// </summary>
        public byte[] CreateVendorBootImage4(byte[] ramdisk, byte[] dtb, string? cmdline, string? product_name, byte[]? bootconfig = null, uint page_size = 4096, uint base_addr = 0x10000000)
        {
            VendorBootImageHeaderV4 header = VendorBootImageHeaderV4.Create();
            header.PageSize = page_size;
            header.KernelAddr = base_addr + 0x00008000;
            header.RamdiskAddr = base_addr + 0x01000000;
            header.TagsAddr = base_addr + 0x00000100;
            header.VendorRamdiskSize = (uint)ramdisk.Length;
            header.DtbSize = (uint)dtb.Length;
            header.DtbAddr = (ulong)base_addr + 0x01100000;
            header.HeaderSize = (uint)Marshal.SizeOf<VendorBootImageHeaderV4>();
            header.BootconfigSize = (uint)(bootconfig?.Length ?? 0);

            if (!string.IsNullOrEmpty(cmdline))
            {
                byte[] cmdBytes = Encoding.ASCII.GetBytes(cmdline);
                Array.Copy(cmdBytes, header.Cmdline, Math.Min(cmdBytes.Length, 2048));
            }

            if (!string.IsNullOrEmpty(product_name))
            {
                byte[] nameBytes = Encoding.ASCII.GetBytes(product_name);
                Array.Copy(nameBytes, header.Name, Math.Min(nameBytes.Length, 16));
            }

            int headerPages = (int)(header.HeaderSize + page_size - 1) / (int)page_size;
            int ramdiskPages = (ramdisk.Length + (int)page_size - 1) / (int)page_size;
            int dtbPages = (dtb.Length + (int)page_size - 1) / (int)page_size;
            int configPages = (int)((header.BootconfigSize + page_size - 1) / (int)page_size);

            int totalSize = (headerPages + ramdiskPages + dtbPages + configPages) * (int)page_size;
            byte[] buffer = new byte[totalSize];

            byte[] headerBytes = DataHelper.Struct2Bytes(header);
            Array.Copy(headerBytes, 0, buffer, 0, headerBytes.Length);
            Array.Copy(ramdisk, 0, buffer, headerPages * page_size, ramdisk.Length);
            Array.Copy(dtb, 0, buffer, (headerPages + ramdiskPages) * page_size, dtb.Length);
            if (bootconfig != null)
                Array.Copy(bootconfig, 0, buffer, (headerPages + ramdiskPages + dtbPages) * page_size, bootconfig.Length);

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
        /// 校验 android-info.txt 中的要求
        /// </summary>
        public bool VerifyRequirements(string infoText, bool force = false)
        {
            var parser = new ProductInfoParser(this);
            if (!parser.Validate(infoText, out string? error))
            {
                if (force)
                {
                    NotifyCurrentStep("WARNING: Requirements not met (ignored): " + error);
                    return true;
                }
                throw new Exception(error);
            }
            return true;
        }

        /// <summary>
        /// 刷入 VBMeta 镜像并可选禁用校验 (对应 --disable-verity / --disable-verification)
        /// </summary>
        public FastbootResponse FlashVbmeta(string partition, string filePath, bool disableVerity = false, bool disableVerification = false)
        {
            if (!File.Exists(filePath)) throw new FileNotFoundException(filePath);
            byte[] data = File.ReadAllBytes(filePath);

            if (data.Length < Marshal.SizeOf<VbmetaHeader>())
                throw new Exception("vbmeta image too small");

            if (data.Length >= 64)
            {
                byte[] footerBytes = new byte[64];
                Array.Copy(data, data.Length - 64, footerBytes, 0, 64);
                try {
                    var footer = AvbFooter.FromBytes(footerBytes);
                    if (footer.IsValid()) {
                        NotifyCurrentStep($"AVB Footer detected (Vbmeta origin size: {footer.OriginalImageSize}, Vbmeta size: {footer.VbmetaSize})");
                    }
                } catch { }
            }

            if (disableVerity || disableVerification)
            {
                var header = VbmetaHeader.FromBytes(data);
                if (header.Magic[0] == (byte)'A' && header.Magic[1] == (byte)'V' && header.Magic[2] == (byte)'B' && header.Magic[3] == (byte)'0')
                {
                    if (disableVerity) header.Flags |= VbmetaFlags.AVB_VBMETA_IMAGE_FLAGS_HASHTREE_DISABLED;
                    if (disableVerification) header.Flags |= VbmetaFlags.AVB_VBMETA_IMAGE_FLAGS_VERIFICATION_DISABLED;

                    byte[] headerBytes = DataHelper.Struct2Bytes(header);
                    Array.Copy(headerBytes, 0, data, 0, headerBytes.Length);
                    NotifyCurrentStep($"Modified VBMeta flags: verity={disableVerity}, verification={disableVerification}");
                }
            }

            return FlashUnsparseImage(partition, new MemoryStream(data), data.Length);
        }

        /// <summary>
        /// 更新 Super 分区元数据 (对应 update-super)
        /// </summary>
        public FastbootResponse UpdateSuper(string partition, string metadataPath)
        {
            if (!File.Exists(metadataPath)) throw new FileNotFoundException(metadataPath);
            
            // 如果是 super_empty.img，我们需要提取元数据部分发送给设备
            LpMetadata metadata = MetadataReader.ReadFromImageFile(metadataPath);
            byte[] metadataBlob = MetadataWriter.SerializeMetadata(metadata);
            
            NotifyCurrentStep($"Updating super metadata for {partition}");
            DownloadData(metadataBlob).ThrowIfError();
            return RawCommand("update-super:" + partition);
        }

        /// <summary>
        /// 清除 Super 分区元数据
        /// </summary>
        public FastbootResponse WipeSuper(string partition) => RawCommand("wipe-super:" + partition);

        /// <summary>
        /// 基于 fastboot-info.txt 执行刷机操作
        /// </summary>
        public void FlashFromInfo(string infoContent, string imageDir, bool wipe = false, string? slotOverride = null, bool optimizeSuper = true)
        {
            NotifyCurrentStep("Parsing fastboot-info.txt...");
            var lines = infoContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string currentSlot = slotOverride ?? GetCurrentSlot();
            string otherSlot = currentSlot == "a" ? "b" : "a";

            // 预加载动态分区元数据以优化 IsLogical 速度
            LoadLogicalPartitionsFromMetadata(Path.Combine(imageDir, "super_empty.img"));

            var commands = new List<List<string>>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;

                var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                if (parts.Count == 0) continue;

                if (parts[0] == "if-wipe")
                {
                    if (!wipe) continue;
                    parts.RemoveAt(0);
                }
                if (parts.Count > 0) commands.Add(parts);
            }

            // AOSP Optimization: If we find logical partitions to flash, resize them all to 0 first 
            // to ensure optimal placement in the super partition (when not using optimized super flash).
            if (IsUserspace())
            {
                 foreach (var cmdParts in commands)
                 {
                     if (cmdParts[0] == "flash")
                     {
                         // Find partition name in arguments
                         string? part = GetPartitionFromArgs(cmdParts.GetRange(1, cmdParts.Count - 1));
                         if (part != null && IsLogicalOptimized(part))
                         {
                             try { ResizeLogicalPartition(part, 0); } catch { }
                         }
                     }
                 }
            }

            // AOSP Optimized Flash Super: Group logical partitions and flash them more efficiently
            if (optimizeSuper && IsUserspace())
            {
                string emptyPath = Path.Combine(imageDir, "super_empty.img");
                if (File.Exists(emptyPath))
                {
                    var logicalPartitionsToFlash = new List<(string Name, string Path)>();
                    for (int i = 0; i < commands.Count; i++)
                    {
                        var parts = commands[i];
                        if (parts[0] == "flash")
                        {
                            string? part = GetPartitionFromArgs(parts.GetRange(1, parts.Count - 1));
                            string? imgName = parts.Count > 2 ? parts[2] : part + ".img"; // Simple heuristic for img name
                            if (part != null && IsLogicalOptimized(part))
                            {
                                string imgPath = Path.Combine(imageDir, imgName!);
                                if (File.Exists(imgPath))
                                {
                                    logicalPartitionsToFlash.Add((part, imgPath));
                                    // Remove from original commands so we don't flash it twice
                                    commands.RemoveAt(i);
                                    i--;
                                }
                            }
                        }
                    }

                    if (logicalPartitionsToFlash.Count > 0)
                    {
                        NotifyCurrentStep("Optimizing super partition flash from info...");
                        var helper = new SuperFlashHelper(this, "super", emptyPath);
                        foreach (var (name, path) in logicalPartitionsToFlash)
                        {
                            helper.AddPartition(name, path);
                        }
                        helper.Flash();
                    }
                }
            }

            foreach (var parts in commands)
            {
                string cmd = parts[0];
                var args = parts.GetRange(1, parts.Count - 1);

                switch (cmd)
                {
                    case "version":
                        if (args.Count > 0 && !CheckFastbootInfoRequirements(args[0]))
                            NotifyCurrentStep($"WARNING: Unsupported fastboot-info.txt version: {args[0]}");
                        break;
                    case "flash":
                        ExecuteFlashTaskFromInfo(args, imageDir, currentSlot, otherSlot);
                        break;
                    case "erase":
                        if (args.Count > 0) ErasePartition(args[0]);
                        break;
                    case "reboot":
                        if (args.Count > 0) Reboot(args[0]);
                        else Reboot();
                        break;
                    case "update-super":
                        string target = args.Count > 0 ? args[0] : "super";
                        string emptyPath = Path.Combine(imageDir, "super_empty.img");
                        if (File.Exists(emptyPath)) UpdateSuper(target, emptyPath);
                        break;
                    default:
                        NotifyCurrentStep($"Unknown command in fastboot-info.txt: {cmd}");
                        break;
                }
            }
        }

        private string? GetPartitionFromArgs(List<string> args)
        {
            // Simple heuristic to find partition name in flash arguments
            foreach(var arg in args)
            {
                if (!arg.StartsWith("--")) return arg;
            }
            return null;
        }

        private void ExecuteFlashTaskFromInfo(List<string> args, string imageDir, string currentSlot, string otherSlot)
        {
            bool applyVbmeta = false;
            string targetSlot = currentSlot;
            string? partition = null;
            string? imgName = null;

            foreach (var arg in args)
            {
                if (arg == "--apply-vbmeta") applyVbmeta = true;
                else if (arg == "--slot-other") targetSlot = otherSlot;
                else if (partition == null) partition = arg;
                else if (imgName == null) imgName = arg;
            }

            if (partition != null && imgName != null)
            {
                string imgPath = Path.Combine(imageDir, imgName);
                if (File.Exists(imgPath))
                {
                    // AOSP: 如果是动态分区，为了保证最优空间分配，首先调整大小为 0。
                    // ResizeLogicalPartition 内部会自动确保设备处于 fastbootd (userspace) 状态。
                    if (IsLogicalOptimized(partition))
                    {
                         try { ResizeLogicalPartition(partition, 0); } catch { }
                    }

                    if (applyVbmeta || IsVbmetaPartition(partition))
                        FlashVbmeta(partition, imgPath);
                    else
                        FlashImage(partition, imgPath, targetSlot);
                }
                else
                {
                    NotifyCurrentStep($"WARNING: Image {imgName} for {partition} not found in {imageDir}");
                }
            }
        }

        public bool IsVbmetaPartition(string partition)
        {
            return partition.StartsWith("vbmeta", StringComparison.OrdinalIgnoreCase);
        }

        public bool CheckFastbootInfoRequirements(string version)
        {
            if (uint.TryParse(version, out uint v)) return v <= 2; // Support up to version 2
            return false;
        }

        /// <summary>
        /// 执行 FlashAll (在指定目录下寻找并刷入基础分区)
        /// </summary>
        public void FlashAll(string productOutDir, bool wipe = false, bool skipSecondary = false, bool force = false, bool optimizeSuper = true)
        {
            CancelSnapshotIfNeeded();
            
            // 预加载动态分区元数据以优化 IsLogical 速度
            LoadLogicalPartitionsFromMetadata(Path.Combine(productOutDir, "super_empty.img"));

            string infoPath = Path.Combine(productOutDir, "fastboot-info.txt");
            if (File.Exists(infoPath))
            {
                NotifyCurrentStep("Using fastboot-info.txt for flashing...");
                FlashFromInfo(File.ReadAllText(infoPath), productOutDir, wipe, null, optimizeSuper);
                if (wipe) WipeUserData();
                return;
            }

            string productInfoPath = Path.Combine(productOutDir, "android-info.txt");
            if (File.Exists(productInfoPath))
            {
                VerifyRequirements(File.ReadAllText(productInfoPath), force);
            }

            var imageFiles = Directory.GetFiles(productOutDir, "*.img").ToList();
            List<string> physicalImages = new List<string>();
            List<string> logicalImages = new List<string>();

            foreach (var f in imageFiles)
            {
                string part = Path.GetFileNameWithoutExtension(f);
                if (IsLogicalOptimized(part)) logicalImages.Add(f);
                else physicalImages.Add(f);
            }

            physicalImages = physicalImages.OrderBy(f => {
                string part = Path.GetFileNameWithoutExtension(f);
                if (part.EndsWith("_other", StringComparison.OrdinalIgnoreCase)) part = part.Substring(0, part.Length - 6);
                int index = Array.IndexOf(PartitionPriority, part.ToLower());
                return index == -1 ? int.MaxValue : index;
            }).ToList();

            string currentSlot = GetCurrentSlot();

            foreach (var filePath in physicalImages)
            {
                string fileName = Path.GetFileNameWithoutExtension(filePath);
                string part = fileName;
                string targetSlot = currentSlot;
                bool isOther = false;

                if (fileName.EndsWith("_other", StringComparison.OrdinalIgnoreCase))
                {
                    part = fileName.Substring(0, fileName.Length - 6);
                    targetSlot = currentSlot == "a" ? "b" : "a";
                    isOther = true;
                }

                // 刷入指定插槽
                if (IsVbmetaPartition(part)) FlashVbmeta(part, filePath);
                else FlashImage(part, filePath, targetSlot);

                // 如果不是 _other 镜像，且不跳过第二插槽且该分区支持 A/B
                if (!isOther && !skipSecondary && HasSlot(part))
                {
                    string otherSlot = currentSlot == "a" ? "b" : "a";
                    if (IsVbmetaPartition(part)) FlashVbmeta(part, filePath);
                    else FlashImage(part, filePath, otherSlot);
                }

                string sigPath = Path.Combine(productOutDir, fileName + ".sig");
                if (File.Exists(sigPath))
                {
                    Signature(File.ReadAllBytes(sigPath));
                }
            }

            if (logicalImages.Count > 0)
            {
                if (optimizeSuper && IsUserspace())
                {
                    NotifyCurrentStep("Optimizing super partition flash...");
                    string emptyPath = Path.Combine(productOutDir, "super_empty.img");
                    var helper = new SuperFlashHelper(this, "super", File.Exists(emptyPath) ? emptyPath : null);
                    foreach (var logImg in logicalImages)
                    {
                        helper.AddPartition(Path.GetFileNameWithoutExtension(logImg), logImg);
                    }
                    helper.Flash();
                }
                else
                {
                    // AOSP: Before flashing logical partitions individually, resize them to 0
                    // to ensure optimal placement in the super partition.
                    foreach (var logImg in logicalImages)
                    {
                        string part = Path.GetFileNameWithoutExtension(logImg);
                        if (IsLogicalOptimized(part))
                        {
                            try { ResizeLogicalPartition(part, 0); } catch { }
                        }
                    }

                    foreach (var logImg in logicalImages)
                    {
                        FlashImage(Path.GetFileNameWithoutExtension(logImg), logImg);
                    }
                }
            }

            if (wipe)
            {
                WipeUserData();
            }
        }

        /// <summary>
        /// 清除用户数据、缓存及元数据分区 (对应 fastboot -w)
        /// </summary>
        public void WipeUserData()
        {
            try { FormatPartition("userdata"); } catch { }
            try { FormatPartition("cache"); } catch { }
            try { FormatPartition("metadata"); } catch { }
        }
    }
}
