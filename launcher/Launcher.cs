using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CanonFix;

/// <summary>Основной сценарий: 7 шагов от «голого» ПК до открытого окна Claude Code.</summary>
internal static class Launcher
{
    const int TotalSteps = 7;

    public static int Run()
    {
        // Папки нужны с самого начала — в C:\fix пишется журнал
        try
        {
            Directory.CreateDirectory(Program.BackupDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось создать {Program.BackupDir}: {ex.Message}");
            Pause();
            return 1;
        }
        Log.Init(Program.LogFile);

        PrintHeader();
        if (!IsAdministrator())
            Log.Warn("Внимание: программа запущена НЕ от администратора. Часть шагов может не сработать.");

        Step(1, "Новая консоль Windows", EnableConsoleV2);

        string? claude = Step(2, "Claude Code", EnsureClaude);
        if (claude is null)
        {
            Log.Error("Без Claude Code продолжать нельзя. Подробности — в " + Program.LogFile);
            Pause();
            return 1;
        }

        Step(3, "PATH пользователя", EnsureUserPath);
        Step(4, "Скилл " + Program.SkillName, InstallSkill);
        Step(5, "Рабочая папка", () => Log.Info($"  Папки {Program.FixDir} и {Program.BackupDir} готовы. Отчёты и резервные копии будут там."));

        string prompt = Step(6, "Что не работает?", AskSymptom) ?? BuildPrompt("нужна только диагностика. Проведи диагностику по всем уровням, ничего не меняй и сохрани отчёт");

        PrintHints();
        Console.Write("Нажмите Enter, чтобы открыть Claude Code… ");
        ReadLineSafe();

        bool launched = Step(7, "Запуск Claude Code", () => LaunchClaude(claude, prompt));
        Console.WriteLine();
        if (launched)
        {
            Log.Ok("Готово. Claude Code работает в отдельном окне — это окно можно закрыть.");
        }
        else
        {
            Log.Error("Claude Code не запустился. Попробуйте вручную: откройте cmd в C:\\fix и выполните:");
            Log.Error($"  \"{claude}\" \"{prompt}\"");
        }
        Log.Info("Журнал запускалки: " + Program.LogFile);
        Pause();
        return launched ? 0 : 1;
    }

    // ---------------------------------------------------------------- шаги

    /// <summary>Шаг 1. HKCU\Console\ForceV2 = 1: в старой консоли не работает вставка кода при входе.</summary>
    static void EnableConsoleV2()
    {
        using var key = Registry.CurrentUser.CreateSubKey("Console", writable: true)
                        ?? throw new InvalidOperationException("не открыть HKCU\\Console");

        object? current = key.GetValue("ForceV2");
        if (current is int v && v == 1)
        {
            Log.Info("  Новая консоль уже включена (ForceV2 = 1).");
            return;
        }

        key.SetValue("ForceV2", 1, RegistryValueKind.DWord);
        Log.Info($"  Включена новая консоль Windows (ForceV2: {current ?? "не было"} → 1). Действует для новых окон.");
    }

    /// <summary>Шаг 2. Claude Code в %USERPROFILE%\.local\bin, при отсутствии — официальный установщик.</summary>
    static string? EnsureClaude()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string exe = Path.Combine(userProfile, ".local", "bin", "claude.exe");

        if (File.Exists(exe))
        {
            Log.Info("  Claude Code найден: " + exe);
            LogClaudeVersion(exe);
            return exe;
        }

        Log.Warn("  Claude Code не найден. Ставлю официальным установщиком (irm https://claude.ai/install.ps1 | iex).");
        Log.Warn("  Сейчас скачается около 180 МБ. Прогресс НЕ показывается — окно может выглядеть");
        Log.Warn("  зависшим несколько минут. Это нормально, просто ждите.");
        Console.WriteLine();

        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; irm https://claude.ai/install.ps1 | iex");

        var sw = Stopwatch.StartNew();
        using (var p = Process.Start(psi) ?? throw new InvalidOperationException("не удалось запустить powershell.exe"))
        {
            p.WaitForExit();
            Log.Debug($"install.ps1 завершился с кодом {p.ExitCode} за {sw.Elapsed:mm\\:ss}");
        }
        Console.WriteLine();

        if (File.Exists(exe))
        {
            Log.Ok("  Claude Code установлен: " + exe);
            LogClaudeVersion(exe);
            return exe;
        }

        // Запасной вариант: уже стоит другим способом (winget и т. п.) и есть в PATH
        string? onPath = FindOnPath("claude.exe");
        if (onPath is not null)
        {
            Log.Warn("  В %USERPROFILE%\\.local\\bin claude.exe нет, но найден в PATH: " + onPath);
            LogClaudeVersion(onPath);
            return onPath;
        }

        Log.Error("  Claude Code так и не появился. Проверьте интернет и попробуйте вручную в PowerShell:");
        Log.Error("    irm https://claude.ai/install.ps1 | iex");
        return null;
    }

    /// <summary>Шаг 3. Добавить %USERPROFILE%\.local\bin в PATH пользователя (HKCU\Environment), не теряя REG_EXPAND_SZ.</summary>
    static void EnsureUserPath()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string binDir = Path.Combine(userProfile, ".local", "bin");

        using var env = Registry.CurrentUser.OpenSubKey("Environment", writable: true)
                        ?? throw new InvalidOperationException("не открыть HKCU\\Environment");

        string raw = env.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        RegistryValueKind kind;
        try { kind = env.GetValueKind("Path"); }
        catch (IOException) { kind = RegistryValueKind.ExpandString; }
        if (kind != RegistryValueKind.String && kind != RegistryValueKind.ExpandString)
            kind = RegistryValueKind.ExpandString;

        bool present = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(p => SamePath(Environment.ExpandEnvironmentVariables(p), binDir));

        if (present)
        {
            Log.Info("  PATH уже содержит " + binDir);
        }
        else
        {
            string updated = raw.Length == 0 ? binDir : raw.TrimEnd(';') + ";" + binDir;
            env.SetValue("Path", updated, kind);
            BroadcastEnvironmentChange();
            Log.Info("  В PATH пользователя добавлено " + binDir);
        }

        // И в текущий процесс — дочернее окно унаследует
        string procPath = Environment.GetEnvironmentVariable("Path") ?? "";
        if (!procPath.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(p => SamePath(p, binDir)))
            Environment.SetEnvironmentVariable("Path", procPath.TrimEnd(';') + ";" + binDir);
    }

    /// <summary>Шаг 4. Скилл: свежая версия из репозитория или встроенная копия.</summary>
    static void InstallSkill()
    {
        var r = SkillInstaller.Install();
        string was = r.PreviousVersion is null ? "раньше не было"
                   : r.PreviousVersion == r.Version ? "та же версия была и раньше"
                   : $"раньше была {r.PreviousVersion}";
        Log.Ok($"  Скилл {Program.SkillName} установлен: версия {r.Version} ({r.Source}; {was}).");
        Log.Info("  Папка: " + r.TargetDir);
    }

    /// <summary>Шаг 6. Меню симптомов → начальный промпт для Claude.</summary>
    static string AskSymptom()
    {
        Console.WriteLine();
        Console.WriteLine("  1 — сканер (не сканирует, «сканер не в сети», «связь не установлена»)");
        Console.WriteLine("  2 — печать (не печатает, задания висят, выходят пустые листы)");
        Console.WriteLine("  3 — ничего не работает (ни печать, ни сканер)");
        Console.WriteLine("  4 — другое (описать своими словами)");
        Console.WriteLine("  5 — только диагностика, ничего не менять");
        Console.WriteLine();

        while (true)
        {
            Console.Write("Выбор [1-5]: ");
            string choice = (ReadLineSafe() ?? "").Trim();
            string? symptom = choice switch
            {
                "1" => "не работает сканер (не сканирует / «сканер не в сети» / «связь не установлена»). Начни с диагностики",
                "2" => "не работает печать (не печатает, задания висят в очереди или выходят пустые листы). Начни с диагностики",
                "3" => "не работает ничего — ни печать, ни сканер. Начни с диагностики",
                "4" => AskFreeText(),
                "5" => "нужна только диагностика. Проведи диагностику по всем уровням, ничего не меняй и сохрани отчёт",
                _ => null,
            };
            if (symptom is null)
            {
                Console.WriteLine("Введите цифру от 1 до 5.");
                continue;
            }

            Log.Debug($"выбор меню: {choice}; симптом: {symptom}");
            return BuildPrompt(symptom);
        }
    }

    static string? AskFreeText()
    {
        Console.Write("Опишите одной строкой, что не работает: ");
        string text = SanitizeForCmd(ReadLineSafe() ?? "");
        if (text.Length == 0)
        {
            Console.WriteLine("Пустое описание.");
            return null;
        }
        return text + ". Начни с диагностики";
    }

    static string BuildPrompt(string symptom) =>
        $"Используй скилл {Program.SkillName}. На этом ПК: {symptom}.";

    /// <summary>Шаг 7. Claude Code в НОВОМ окне консоли, рабочая папка C:\fix. cmd /k — чтобы окно не закрылось при ошибке на старте.</summary>
    static void LaunchClaude(string claudeExe, string prompt)
    {
        string inner = $"chcp 65001>nul & cd /d \"{Program.FixDir}\" & \"{claudeExe}\" \"{prompt}\"";
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k \"{inner}\"",
            UseShellExecute = true,          // отдельное окно консоли
            WorkingDirectory = Program.FixDir,
            WindowStyle = ProcessWindowStyle.Normal,
        };
        Log.Debug("запуск: cmd.exe " + psi.Arguments);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start вернул null");
        Log.Info($"  Claude Code запущен в новом окне (PID {p.Id}), папка {Program.FixDir}.");
    }

    // ---------------------------------------------------------------- вывод

    static void PrintHeader()
    {
        Console.WriteLine("==============================================================");
        Console.WriteLine($"  CanonFix {Program.AppVersion} — починка Canon MF460 по USB");
        Console.WriteLine($"  Ставит Claude Code и скилл {Program.SkillName}, запускает диагностику");
        Console.WriteLine("==============================================================");
        Console.WriteLine("Нужен интернет (для установки Claude Code и входа в аккаунт).");
        Console.WriteLine();
    }

    static void PrintHints()
    {
        Console.WriteLine();
        Console.WriteLine("Сейчас откроется НОВОЕ окно с Claude Code. Что важно:");
        Console.WriteLine(" • При первом запуске Claude откроет браузер для входа в аккаунт. Войдите и вернитесь в окно консоли.");
        Console.WriteLine(" • Если Claude попросит вставить код: скопируйте его в браузере, затем в окне консоли вставьте");
        Console.WriteLine("   ПРАВОЙ кнопкой мыши или Ctrl+V. Код может НЕ отображаться — это нормально. После вставки нажмите Enter.");
        Console.WriteLine(" • На вопрос про доверие к папке C:\\fix («Do you trust the files in this folder?») — Yes / Enter.");
        Console.WriteLine(" • Дальше Claude сам начнёт диагностику и будет спрашивать разрешение на каждую команду.");
        Console.WriteLine("   Сначала он только смотрит; менять что-либо будет только после вашего согласия.");
        Console.WriteLine($" • Отчёты и резервные копии: {Program.FixDir}. Журнал этой программы: {Program.LogFile}.");
        Console.WriteLine();
    }

    // ---------------------------------------------------------------- вспомогательное

    /// <summary>Выполняет шаг, ловит и журналирует ошибки; при ошибке возвращает default.</summary>
    static T? Step<T>(int number, string title, Func<T> action)
    {
        Console.WriteLine($"[{number}/{TotalSteps}] {title}");
        Log.Debug($"--- шаг {number}: {title}");
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            Log.Exception($"  Ошибка на шаге {number} ({title})", ex);
            return default;
        }
    }

    static bool Step(int number, string title, Action action) =>
        Step(number, title, () => { action(); return true; });

    static void LogClaudeVersion(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--version");

            using var p = Process.Start(psi);
            if (p is null) return;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                Log.Debug("claude --version не ответил за 30 с");
                return;
            }
            string version = output.GetAwaiter().GetResult().Trim();
            if (version.Length > 0) Log.Info("  Версия: " + version);
        }
        catch (Exception ex)
        {
            Log.Debug("claude --version: " + ex.Message);
        }
    }

    static string? FindOnPath(string fileName)
    {
        string path = Environment.GetEnvironmentVariable("Path") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* кривые записи в PATH */ }
        }
        return null;
    }

    static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Свободный текст уйдёт в cmd /k — убираем всё, что cmd может понять по-своему.</summary>
    static string SanitizeForCmd(string text)
    {
        text = Regex.Replace(text, @"[""&|<>^%\r\n\t]", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 500 ? text[..500] : text;
    }

    static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    static string? ReadLineSafe()
    {
        try { return Console.ReadLine(); }
        catch { return null; }
    }

    static void Pause()
    {
        Console.WriteLine();
        Console.Write("Нажмите любую клавишу для выхода… ");
        try { Console.ReadKey(intercept: true); }
        catch { ReadLineSafe(); }
        Console.WriteLine();
    }

    // WM_SETTINGCHANGE, чтобы новые процессы (Проводник, новые консоли) увидели обновлённый PATH
    static void BroadcastEnvironmentChange()
    {
        try
        {
            const uint WM_SETTINGCHANGE = 0x001A;
            const uint SMTO_ABORTIFHUNG = 0x0002;
            SendMessageTimeout(new IntPtr(0xFFFF), WM_SETTINGCHANGE, UIntPtr.Zero, "Environment", SMTO_ABORTIFHUNG, 5000, out _);
        }
        catch (Exception ex)
        {
            Log.Debug("WM_SETTINGCHANGE: " + ex.Message);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
        uint flags, uint timeoutMs, out UIntPtr result);
}
