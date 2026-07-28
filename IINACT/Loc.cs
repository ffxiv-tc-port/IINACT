using System.Text;

namespace IINACT;

// Minimal self-contained localization helper mirroring ECommons.LanguageHelpers:
// same ini format (English==translation, one entry per line, literal \n escapes,
// ?? positional placeholders) and the same .Loc() string extension name.
// IINACT references no loc library at all, so we ship this tiny equivalent instead
// of pulling one in. The store format is identical to the rest of the fork set, so
// swapping to real ECommons later would be drop-in.
//
// Scope note: this only covers IINACT's own UI. The vendored OverlayPlugin.Core /
// OverlayPlugin.Common / NotACT / machina trees keep their own upstream resources
// and are deliberately left alone.
public static class Localization
{
    private static readonly Dictionary<string, string> Translations = new();

    public static void Init(string? directory)
    {
        Translations.Clear();
        if (directory == null)
            return;
        var path = Path.Combine(directory, "LanguageChineseTraditional.ini");
        try
        {
            if (!File.Exists(path))
                return;
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var idx = line.IndexOf("==", StringComparison.Ordinal);
                if (idx <= 0)
                    continue;
                var key = line[..idx].Replace("\\n", "\n");
                var value = line[(idx + 2)..].TrimEnd('\r').Replace("\\n", "\n");
                Translations[key] = value;
            }

            Plugin.Log.Information($"Localization: loaded {Translations.Count} entries from {path}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Localization: failed to load {path}");
        }
    }

    public static string Loc(this string s) => Translations.TryGetValue(s, out var t) ? t : s;

    public static string Loc(this string s, params object?[] args)
    {
        var result = s.Loc();
        foreach (var a in args)
        {
            var idx = result.IndexOf("??", StringComparison.Ordinal);
            if (idx < 0)
                break;
            result = result.Remove(idx, 2).Insert(idx, a?.ToString() ?? "");
        }

        return result;
    }
}
