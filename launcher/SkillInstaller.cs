using System.Net.Http.Headers;
using System.Text;

namespace CanonFix;

/// <summary>
/// Ставит скилл в %USERPROFILE%\.claude\skills\canon-mf460-fix\.
/// Сначала пытается скачать свежую версию из репозитория, при неудаче берёт встроенную копию.
/// </summary>
internal static class SkillInstaller
{
    public sealed record Result(string Version, string Source, string? PreviousVersion, string TargetDir);

    public static Result Install()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string target = Path.Combine(userProfile, ".claude", "skills", Program.SkillName);
        string? previous = ReadVersion(target);

        var embedded = EmbeddedSkill.Files;
        if (embedded.Count == 0)
            throw new InvalidOperationException("в exe нет встроенной копии скилла — сборка повреждена");

        IReadOnlyDictionary<string, byte[]> files;
        string source;

        Log.Info("  Пробую скачать свежую версию скилла из " + Program.RepoUrl + " …");
        var online = TryDownload(embedded.Keys);
        if (online is not null)
        {
            files = online;
            source = "из GitHub";
            Log.Debug($"скачано {online.Count} файл(ов), версия {VersionOf(online)}, встроенная {EmbeddedSkill.Version}");
        }
        else
        {
            files = embedded;
            source = "встроенная копия, интернет недоступен или репозиторий не отвечает";
            Log.Warn("  Скачать не удалось — использую копию, встроенную в exe.");
        }

        WriteAtomically(target, files);
        return new Result(VersionOf(files), source, previous, target);
    }

    /// <summary>Скачивает все файлы; при любой ошибке возвращает null (тогда работаем со встроенной копией).</summary>
    static Dictionary<string, byte[]>? TryDownload(IEnumerable<string> names)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CanonFix/" + Program.AppVersion.Split(' ')[0]);
            http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };

            // VERSION первым: если репозиторий недоступен, отваливаемся сразу
            var ordered = names.OrderBy(n => n.Equals("VERSION", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in ordered)
            {
                var bytes = http.GetByteArrayAsync(Program.RawBase + name).GetAwaiter().GetResult();
                if (bytes.Length == 0) throw new InvalidDataException($"пустой файл {name}");
                result[name] = bytes;
            }

            var skill = Encoding.UTF8.GetString(result["SKILL.md"]);
            if (!skill.TrimStart('﻿').StartsWith("---", StringComparison.Ordinal))
                throw new InvalidDataException("SKILL.md без frontmatter — похоже, скачалось не то");

            return result;
        }
        catch (Exception ex)
        {
            Log.Debug("download failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Пишет во временную папку рядом, потом подменяет целевую — чтобы не остаться с половиной файлов.</summary>
    static void WriteAtomically(string target, IReadOnlyDictionary<string, byte[]> files)
    {
        string parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string tmp = $"{target}.tmp-{stamp}";
        string old = $"{target}.old-{stamp}";

        Directory.CreateDirectory(tmp);
        foreach (var (name, bytes) in files)
            File.WriteAllBytes(Path.Combine(tmp, name), bytes);

        if (Directory.Exists(target))
        {
            Directory.Move(target, old);
            try
            {
                Directory.Move(tmp, target);
            }
            catch
            {
                Directory.Move(old, target); // вернуть как было
                throw;
            }
            try { Directory.Delete(old, recursive: true); }
            catch (Exception ex) { Log.Debug("не удалось удалить старую копию: " + ex.Message); }
        }
        else
        {
            Directory.Move(tmp, target);
        }
    }

    static string VersionOf(IReadOnlyDictionary<string, byte[]> files) =>
        files.TryGetValue("VERSION", out var b) ? Encoding.UTF8.GetString(b).Trim() : "?";

    static string? ReadVersion(string dir)
    {
        try
        {
            string path = Path.Combine(dir, "VERSION");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
