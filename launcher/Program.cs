using System.Reflection;
using System.Runtime.Versioning;
using System.Text;

[assembly: SupportedOSPlatform("windows")]

namespace CanonFix;

internal static class Program
{
    public const string SkillName = "canon-mf460-fix";
    public const string RepoUrl = "https://github.com/Bvivg/canon-fix";
    public const string RawBase = "https://raw.githubusercontent.com/Bvivg/canon-fix/main/skills/" + SkillName + "/";
    public const string FixDir = @"C:\fix";
    public const string BackupDir = @"C:\fix\backup";
    public const string LogFile = @"C:\fix\launcher.log";

    public static string AppVersion { get; } = ComputeVersion();

    static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        catch { /* нет консоли — не критично */ }

        if (args.Any(a => a is "--version" or "-v"))
        {
            Console.WriteLine($"CanonFix {AppVersion}");
            return 0;
        }
        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            PrintHelp();
            return 0;
        }
        if (args.Any(a => a == "--selftest"))
            return SelfTest.Run();

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("CanonFix работает только на Windows. Для проверки сборки: CanonFix --selftest");
            return 2;
        }

        return Launcher.Run();
    }

    static void PrintHelp()
    {
        Console.WriteLine($"CanonFix {AppVersion} — запускалка Claude Code со скиллом {SkillName}");
        Console.WriteLine();
        Console.WriteLine("Без параметров: полный сценарий (консоль, Claude Code, PATH, скилл, C:\\fix, меню, запуск).");
        Console.WriteLine("  --selftest   проверить exe и встроенный скилл, ничего не меняя в системе");
        Console.WriteLine("  --version    показать версию");
        Console.WriteLine();
        Console.WriteLine("Репозиторий: " + RepoUrl);
    }

    // "1.0.23+abcdef0123..." → "1.0.23 (abcdef0)"
    static string ComputeVersion()
    {
        var asm = typeof(Program).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
            return asm.GetName().Version?.ToString() ?? "?";

        int plus = info.IndexOf('+');
        if (plus < 0) return info;

        string sha = info[(plus + 1)..];
        if (sha.Length > 7) sha = sha[..7];
        return $"{info[..plus]} ({sha})";
    }
}
