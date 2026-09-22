using System.Text;

namespace CanonFix;

/// <summary>Вывод в консоль + журнал C:\fix\launcher.log. Debug пишется только в файл.</summary>
internal static class Log
{
    static readonly object Gate = new();
    static string? _path;

    public static void Init(string path)
    {
        _path = path;
        WriteFile($"===== CanonFix {Program.AppVersion} — запуск, {Environment.MachineName}, {Environment.OSVersion} =====");
    }

    public static void Info(string message)
    {
        Console.WriteLine(message);
        WriteFile("INFO  " + message);
    }

    public static void Warn(string message)
    {
        WriteColored(message, ConsoleColor.Yellow);
        WriteFile("WARN  " + message);
    }

    public static void Error(string message)
    {
        WriteColored(message, ConsoleColor.Red);
        WriteFile("ERROR " + message);
    }

    public static void Ok(string message)
    {
        WriteColored(message, ConsoleColor.Green);
        WriteFile("OK    " + message);
    }

    public static void Debug(string message) => WriteFile("DEBUG " + message);

    public static void Exception(string context, Exception ex)
    {
        Error($"{context}: {ex.Message}");
        WriteFile("TRACE " + ex);
    }

    static void WriteColored(string message, ConsoleColor color)
    {
        try
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
        }
        finally
        {
            Console.ResetColor();
        }
    }

    static void WriteFile(string line)
    {
        if (_path is null) return;
        try
        {
            lock (Gate)
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { /* журнал не должен ронять программу */ }
    }
}
