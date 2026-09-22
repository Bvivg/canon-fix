using System.Text;

namespace CanonFix;

/// <summary>Файлы скилла, зашитые в exe при сборке (skills/canon-mf460-fix/*).</summary>
internal static class EmbeddedSkill
{
    const string Prefix = "skill/";

    public static IReadOnlyDictionary<string, byte[]> Files { get; } = Load();

    public static string Version =>
        Files.TryGetValue("VERSION", out var bytes) ? Encoding.UTF8.GetString(bytes).Trim() : "?";

    static Dictionary<string, byte[]> Load()
    {
        var asm = typeof(EmbeddedSkill).Assembly;
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;

            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) continue;

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            result[name[Prefix.Length..]] = ms.ToArray();
        }

        return result;
    }
}
