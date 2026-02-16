namespace LibLpSharp;

public static class LpLogger
{
    public static Action<string>? LogMessage { get; set; }
    public static Action<string>? LogWarning { get; set; }
    public static Action<string>? LogError { get; set; }

    public static void Info(string message) => LogMessage?.Invoke(message);
    public static void Warning(string message) => LogWarning?.Invoke(message);
    public static void Error(string message) => LogError?.Invoke(message);
}
