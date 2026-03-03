using SharpFastboot;
using SharpFastboot.Usb;

namespace Demo
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== SharpFastboot Non-Destructive Command Test ===");

            try
            {
                // 1. 连接设备
                Console.WriteLine("\n[1] Connecting to device...");
                var util = FastbootUtil.WaitForDevice(UsbManager.GetAllDevices, null, 5);
                if (util == null)
                {
                    Console.WriteLine("ERROR: No fastboot device found within 5 seconds.");
                    return;
                }
                Console.WriteLine("Connected to device: " + (util.Transport as UsbDevice)?.SerialNumber);

                // 2. 测试 getvar all
                Console.WriteLine("\n[2] Testing 'getvar all'...");
                var vars = util.GetVarAll();
                foreach (var kv in vars.Take(10))
                {
                    Console.WriteLine($"  {kv.Key}: {kv.Value}");
                }
                if (vars.Count > 10) Console.WriteLine($"  ... and {vars.Count - 10} more variables.");

                // 3. 测试获取特定变量
                Console.WriteLine("\n[3] Testing 'getvar product'...");
                try { Console.WriteLine("  product: " + util.GetVar("product")); } catch { Console.WriteLine("  product: (failed or empty)"); }

                // 4. 测试 gsi status
                Console.WriteLine("\n[4] Testing 'gsi status'...");
                var gsiStatus = util.GsiStatus();
                Console.WriteLine($"  Response: {gsiStatus.Response}");

                // 5. 测试获取当前槽位
                Console.WriteLine("\n[5] Testing 'GetCurrentSlot'...");
                try
                {
                    string slot = util.GetCurrentSlot();
                    Console.WriteLine($"  Current Slot: {slot}");
                }
                catch { Console.WriteLine("  Current Slot: (not supported or failed)"); }

                // 6. 测试 snapshot-update (空参数查询状态)
                Console.WriteLine("\n[6] Testing 'snapshot-update' (query status)...");
                var snapshotResp = util.SnapshotUpdate();
                Console.WriteLine($"  Response: {snapshotResp.Response}");

                // 7. 测试 stage (内存暂存，发送一小段数据验证协议)
                Console.WriteLine("\n[7] Testing 'stage' with dummy data...");
                byte[] dummyData = System.Text.Encoding.UTF8.GetBytes("SharpFastboot Test Data");
                var stageResp = util.Stage(dummyData);
                Console.WriteLine($"  Result: {stageResp.Result}, Message: {stageResp.Response}");

                // 8. 逻辑分区查询 (如果支持)
                if (util.IsUserspace())
                {
                    Console.WriteLine("\n[8] Device in Fastbootd (userspace) mode.");
                }
                else
                {
                    Console.WriteLine("\n[8] Device is NOT in fastbootd mode.");
                }

                Console.WriteLine("\n=== Test Sequence Completed ===");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[FATAL ERROR] {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }
}