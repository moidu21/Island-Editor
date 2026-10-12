using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anno1800IslandTool.Core.Localization;

/// <summary>Translations of authored presentation text. Never processes XML, paths or user input.</summary>
public static class TextCatalog
{
    private static readonly Dictionary<string, string[]> Entries = Load();
    private static readonly Dictionary<string, string> Sources = BuildSources();
    private static readonly Regex Placeholder = new(@"(?<!\{)\{(\d+)([^{}]*)\}(?!\})", RegexOptions.CultureInvariant);
    private static string _language = "fr";
    private static readonly object RenderLock = new();
    private static readonly Dictionary<string, string[]> Rendered = new(StringComparer.Ordinal);
    private static readonly Queue<string> RenderOrder = new();
    public static string Language { get => Volatile.Read(ref _language); set => Volatile.Write(ref _language, value is "en" or "de" ? value : "fr"); }

    public static string T(string source) => Translate(source, Language);

    public static string Translate(string source, string language)
    {
        if (string.IsNullOrEmpty(source) || !Entries.TryGetValue(source, out var values)) return source;
        if (language == "fr") return values.Length > 2 ? values[2] : source;
        return values[language == "de" ? 1 : 0];
    }

    /// <summary>Localizes a format before rendering; values and original format specifiers remain intact.</summary>
    public static string F(FormattableString message)
    {
        var formats = new Dictionary<string, string>();
        var key = Placeholder.Replace(message.Format, match =>
        {
            formats[match.Groups[1].Value] = match.Groups[2].Value;
            return "{" + match.Groups[1].Value + "}";
        });
        if (!Entries.ContainsKey(key)) return message.ToString(CultureInfo.CurrentCulture);
        string Render(string text)
        {
            var format = Placeholder.Replace(text, match => "{" + match.Groups[1].Value + formats.GetValueOrDefault(match.Groups[1].Value, "") + "}");
            return string.Format(CultureInfo.CurrentCulture, format, message.GetArguments());
        }
        var variants = new[] { Render(Translate(key, "fr")), Render(Translate(key, "en")), Render(Translate(key, "de")) };
        lock (RenderLock)
        {
            foreach (var variant in variants)
            {
                if (!Rendered.ContainsKey(variant)) RenderOrder.Enqueue(variant);
                Rendered[variant] = variants;
            }
            // Keep text only, never references to a project or a mesh, and bound long-session memory use.
            while (RenderOrder.Count > 12000) Rendered.Remove(RenderOrder.Dequeue());
        }
        return variants[Language == "fr" ? 0 : Language == "de" ? 2 : 1];
    }

    /// <summary>Refreshes previously authored presentation text after a language change.</summary>
    public static string Display(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        lock (RenderLock)
            if (Rendered.TryGetValue(text, out var variants)) return variants[Language == "fr" ? 0 : Language == "de" ? 2 : 1];
        return T(SourceOf(text));
    }

    public static string DisplayLines(string text)
    {
        var direct = Display(text);
        if (direct != text || string.IsNullOrEmpty(text)) return direct;
        return Regex.Replace(text, @"[^\r\n]+", match => Display(match.Value));
    }

    // Reverse lookup is restricted to authored controls, never model values or user-entered text.
    public static string SourceOf(string text) => Sources.GetValueOrDefault(text, text);

    private static Dictionary<string, string[]> Load()
    {
        using var stream = typeof(TextCatalog).Assembly.GetManifestResourceStream("Anno1800IslandTool.Core.Localization.Catalog.json")
            ?? throw new InvalidOperationException("Missing localization catalogue.");
        return JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)!;
    }
    private static Dictionary<string, string> BuildSources()
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (source, values) in Entries)
            foreach (var value in values) sources.TryAdd(value, source);
        // An original source always wins over an ambiguous reverse translation.
        foreach (var source in Entries.Keys) sources[source] = source;
        return sources;
    }
}
