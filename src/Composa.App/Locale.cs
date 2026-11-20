namespace Composa.App;

/// <summary>Localizes interface captions without changing command IDs or document content.</summary>
internal static class Locale
{
    internal static string Language { get; set; } = "en";
    private static readonly Lazy<Dictionary<string, string>> Russian = new(ReadRussian);

    public static string T(string text)
    {
        if (Language != "ru" || text.Length == 0) return text;
        var entries = Russian.Value;
        if (entries.TryGetValue(text, out var translated)) return translated;
        // A dialog and its menu command often share a caption, with an ellipsis only in the menu.
        if (text.EndsWith('…') && entries.TryGetValue(text[..^1], out translated)) return translated + "…";
        return entries.TryGetValue(text + "…", out translated) ? translated.TrimEnd('…') : text;
    }

    private static Dictionary<string, string> ReadRussian()
    {
        using var stream = typeof(Locale).Assembly.GetManifestResourceStream("Composa.Locale.ru.tsv")
            ?? throw new InvalidOperationException("Missing Russian interface catalog.");
        using var reader = new StreamReader(stream);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            int split = line.IndexOf('\t');
            entries.Add(line[..split], line[(split + 1)..]);
        }
        return entries;
    }
}
