namespace LibSparseSharp;

public static class SparseLogger
{
    public static Action<string>? LogMessage { get; set; }

    public static void Info(string message) => LogMessage?.Invoke($"[INFO] {message}");
    public static void Warn(string message) => LogMessage?.Invoke($"[WARN] {message}");
    public static void Error(string message) => LogMessage?.Invoke($"[ERROR] {message}");
}
