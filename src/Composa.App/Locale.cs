using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Composa.App;

/// <summary>Localizes interface captions without changing command IDs or document content.</summary>
internal static class Locale
{
    internal static string Language { get; set; } = "en";
    private static readonly Lazy<Dictionary<string, string>> Russian = new(ReadRussian);

    private sealed record Source(string English);
    // Only strings handed to the presentation catalog can be rewritten. A layer or file
    // named "Save" must keep its name even if a button with the same text is translated.
    private static readonly ConditionalWeakTable<string, Source> Sources = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), string> Captions = new();

    public static string T(string text)
    {
        if (text.Length == 0) return text;
        if (Sources.TryGetValue(text, out var source)) text = source.English;
        var entries = Russian.Value;
        if (!entries.TryGetValue(text, out var ru))
        {
            if (text.EndsWith('…') && entries.TryGetValue(text[..^1], out ru)) ru += "…";
            else if (entries.TryGetValue(text + "…", out ru)) ru = ru.TrimEnd('…');
            else return text;
        }
        return Captions.GetOrAdd((text, Language), key =>
        {
            var caption = new string((key.Item2 == "ru" ? ru : key.Item1).AsSpan());
            Sources.Add(caption, new(key.Item1));
            return caption;
        });
    }

    public static void Tip(Control control, object? value) =>
        ToolTip.SetTip(control, value is string text ? Message(text) : value);

    private sealed record MessageTemplate(string Prefix, Regex Pattern, string Format, string? ErrorGroup);
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.CultureInvariant);
    private static readonly Lazy<MessageTemplate[]> Templates = new(() => Russian.Value
        .Where(entry => entry.Key.Contains("{0}", StringComparison.Ordinal))
        .Select(entry =>
        {
            var key = entry.Key;
            var pattern = string.Concat(Regex.Split(key, @"(\{\d+\})").Select(part =>
                Placeholder.IsMatch(part) ? "(?<v" + part[1..^1] + ">.+?)" : Regex.Escape(part)));
            var suffix = Regex.Match(key, @": \{(\d+)\}$");
            return new MessageTemplate(key[..key.IndexOf('{')], new Regex("^" + pattern + "$",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking), entry.Value,
                suffix.Success ? "v" + suffix.Groups[1].Value : null);
        }).OrderByDescending(template => template.Prefix.Length).ToArray());

    public static string Message(string text)
    {
        if (Sources.TryGetValue(text, out var original)) text = original.English;
        var exact = T(text);
        if (!ReferenceEquals(text, exact) || text.Length == 0 || text.Length > 24000) return exact;

        bool recognized = false;
        string TranslatePart(string part, int depth = 0)
        {
            var literal = T(part);
            if (!ReferenceEquals(part, literal))
            {
                recognized = true;
                return literal;
            }
            foreach (var template in Templates.Value)
            {
                if (!part.StartsWith(template.Prefix, StringComparison.Ordinal)) continue;
                var match = template.Pattern.Match(part);
                if (!match.Success) continue;
                recognized = true;
                if (Language != "ru") return part;
                return Placeholder.Replace(template.Format, field =>
                {
                    var group = "v" + field.Groups[1].Value;
                    var value = match.Groups[group].Value;
                    return group == template.ErrorGroup && depth < 2 ? TranslatePart(value, depth + 1) : value;
                });
            }
            return part;
        }
        var translated = string.Join("\n", text.ReplaceLineEndings("\n").Split('\n').Select(line =>
            string.Join(" · ", line.Split(" · ").Select(part => TranslatePart(part)))));
        if (!recognized) return text;
        var caption = new string((Language == "ru" ? translated : text).AsSpan());
        Sources.Add(caption, new(text));
        return caption;
    }

    public static void Apply(string code, Window owner, Window? dialog)
    {
        if (Language==code) return;
        Language=code;
        Refresh(owner);
        if(owner is MainWindow main) main.RefreshLanguage();
        if(dialog!=null) Refresh(dialog);
    }

    private static void Refresh(Window window)
    {
        var seen=new HashSet<Control>();
        void Visit(Control c)
        {
            if(!seen.Add(c)) return;
            void Update<T>(StyledProperty<T> property)
            {
                if(c.GetValue(property) is string old && Sources.TryGetValue(old,out var source))
                    c.SetCurrentValue(property,(T)(object)Message(source.English));
            }
            if(c is Window) Update(Window.TitleProperty);
            if(c is TextBlock) Update(TextBlock.TextProperty);
            if(c is ContentControl) Update(ContentControl.ContentProperty);
            if(c is HeaderedContentControl) Update(HeaderedContentControl.HeaderProperty);
            if(c is HeaderedItemsControl) Update(HeaderedItemsControl.HeaderProperty);
            if(c is MenuItem) Update(MenuItem.HeaderProperty);
            if(c is TabItem) Update(TabItem.HeaderProperty);
            if(c is TextBox) Update(TextBox.PlaceholderTextProperty);
            Update(ToolTip.TipProperty);
            if (ToolTip.GetTip(c) is Control tip) Visit(tip);
            Update(Avalonia.Automation.AutomationProperties.NameProperty);
            if(c is ComboBox combo && combo.Items.Count>0 && combo.Items[0] is string first && Sources.TryGetValue(first,out _))
                combo.ItemTemplate=new FuncDataTemplate<string>((item,_)=>new TextBlock {Text=T(item ?? "")});
            if(c is Menu or MenuItem or ContextMenu)
                foreach(var item in ((ItemsControl)c).Items.OfType<Control>()) Visit(item);
            if(c.ContextMenu is {} context) Visit(context);
            foreach(var child in c.GetLogicalChildren().OfType<Control>().ToArray()) Visit(child);
            foreach(var child in c.GetVisualChildren().OfType<Control>().ToArray()) Visit(child);
            c.InvalidateVisual();
        }
        Visit(window);
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
