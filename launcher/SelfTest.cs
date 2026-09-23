using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CanonFix;

/// <summary>
/// CanonFix --selftest: проверяет, что exe собран правильно и скилл внутри на месте.
/// Ничего не меняет в системе — используется в CI и при локальной сборке.
/// </summary>
internal static class SelfTest
{
    static int _failures;

    public static int Run()
    {
        _failures = 0;
        Console.WriteLine($"CanonFix {Program.AppVersion} — самопроверка");
        Console.WriteLine($"ОС: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}");
        Console.WriteLine();

        var files = EmbeddedSkill.Files;
        Console.WriteLine($"Встроенный скилл {Program.SkillName}: {files.Count} файл(ов)");
        foreach (var (name, bytes) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {name,-14} {bytes.Length,8} байт");
        Console.WriteLine();

        Check(files.ContainsKey("SKILL.md"), "есть SKILL.md");
        Check(files.ContainsKey("reference.md"), "есть reference.md");
        Check(files.ContainsKey("VERSION"), "есть VERSION");

        string version = EmbeddedSkill.Version;
        Check(Regex.IsMatch(version, @"^\d{4}\.\d{2}\.\d{2}$"), $"VERSION в формате ГГГГ.ММ.ДД: {version}");

        if (files.TryGetValue("SKILL.md", out var skillBytes))
        {
            string skill = Encoding.UTF8.GetString(skillBytes);
            Check(skill.StartsWith("---", StringComparison.Ordinal), "SKILL.md начинается с frontmatter (без BOM)");
            Check(skill.Contains($"name: {Program.SkillName}", StringComparison.Ordinal), "frontmatter содержит name");
            Check(skill.Contains("description:", StringComparison.Ordinal), "frontmatter содержит description");
            Check(skill.Contains($"version: \"{version}\"", StringComparison.Ordinal), "metadata.version совпадает с VERSION");
            Check(skill.Contains("reference.md", StringComparison.Ordinal), "SKILL.md ссылается на reference.md");
        }

        if (files.TryGetValue("reference.md", out var refBytes))
        {
            string reference = Encoding.UTF8.GetString(refBytes);
            int problems = Regex.Matches(reference, @"^### П\d+\.", RegexOptions.Multiline).Count;
            Check(problems >= 21, $"reference.md содержит каталог проблем: {problems} записей (ожидается не меньше 21)");

            int scripts = Regex.Matches(reference, @"^### S\d+\.", RegexOptions.Multiline).Count;
            Check(scripts >= 12, $"reference.md содержит скрипты: {scripts} штук (ожидается не меньше 12)");

            Check(reference.Contains("## 5. Алгоритм: фазы Ф0–Ф10", StringComparison.Ordinal), "reference.md содержит алгоритм по фазам");
            Check(reference.Contains("## 9. Готово", StringComparison.Ordinal), "reference.md содержит чек-лист «готово»");
        }

        Check(Program.RawBase.EndsWith("/" + Program.SkillName + "/", StringComparison.Ordinal), "URL скилла в репозитории собран верно");

        Console.WriteLine();
        if (_failures == 0)
        {
            Console.WriteLine("Самопроверка пройдена.");
            return 0;
        }
        Console.WriteLine($"Ошибок: {_failures}");
        return 1;
    }

    static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "OK" : "FAIL")}] {what}");
        if (!ok) _failures++;
    }
}
