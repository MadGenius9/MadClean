using System.Net;
using System.Text;

namespace MadClean.Core;

// A bounded, non-rendering parser for the pinned articles' current authored structure.
// It loads no assets, evaluates no script/CSS and has no external-entity resolution.
internal sealed class ManufacturerServiceFactsHtml
{
    private static readonly HashSet<string> VoidTags = new(StringComparer.Ordinal)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };
    private static readonly HashSet<string> SuppressedTags = new(StringComparer.Ordinal)
        { "nav", "header", "footer", "aside", "template", "noscript", "svg", "canvas", "iframe", "script", "style" };
    internal sealed class Element(string name, Dictionary<string, string> attributes, Element? parent)
    {
        internal string Name { get; } = name;
        internal Dictionary<string, string> Attributes { get; } = attributes;
        internal List<object> Children { get; } = [];
        internal Element? Parent { get; } = parent;
        internal bool Suppressed { get; } = parent?.Suppressed == true || SuppressedTags.Contains(name) || attributes.ContainsKey("hidden") ||
            attributes.GetValueOrDefault("aria-hidden")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true ||
            HiddenStyle(attributes.GetValueOrDefault("style"));
        private static bool HiddenStyle(string? style)
        {
            if (style == null) return false;
            string compact = string.Concat(style.Where(character => !char.IsWhiteSpace(character)));
            return compact.Contains("display:none", StringComparison.OrdinalIgnoreCase) || compact.Contains("visibility:hidden", StringComparison.OrdinalIgnoreCase);
        }
        internal IEnumerable<Element> Descendants(string name)
        {
            foreach (var child in Children.OfType<Element>())
            {
                if (child.Suppressed) continue;
                if (child.Name == name) yield return child;
                foreach (var descendant in child.Descendants(name)) yield return descendant;
            }
        }
        internal string Text()
        {
            var result = new StringBuilder(); AppendText(this, result);
            return Normalize(result.ToString());
        }
        private static void AppendText(Element element, StringBuilder result)
        {
            if (element.Suppressed) return;
            foreach (var child in element.Children)
                if (child is string text) result.Append(text);
                else if (child is Element node) AppendText(node, result);
        }
        internal bool HasId(string id) => Attributes.GetValueOrDefault("id") == id;
        internal bool HasClass(string value) => (Attributes.GetValueOrDefault("class") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(value, StringComparer.Ordinal);
    }
    internal Element Main { get; }
    private ManufacturerServiceFactsHtml(Element main) => Main = main;
    internal static ManufacturerServiceFactsHtml Parse(string html, CancellationToken token)
    {
        Element? main = null;
        var stack = new Stack<Element>();
        int position = 0, nodes = 0; bool closed = false;
        token.ThrowIfCancellationRequested();
        while (position < html.Length)
        {
            token.ThrowIfCancellationRequested();
            int start = html.IndexOf('<', position);
            if (start < 0) start = html.Length;
            if (stack.Count > 0 && start > position) stack.Peek().Children.Add(html[position..start]);
            if (start == html.Length) break;
            if (html.AsSpan(start).StartsWith("<!--", StringComparison.Ordinal))
            {
                int endComment = html.IndexOf("-->", start + 4, StringComparison.Ordinal);
                if (endComment < 0) throw new FormatException("Unclosed HTML comment.");
                position = endComment + 3; continue;
            }
            int end = TagEnd(html, start + 1);
            string tag = html[(start + 1)..end]; position = end + 1;
            if (tag.StartsWith('!') || tag.StartsWith('?')) continue;
            bool closing = tag.StartsWith('/');
            bool selfClosing = tag.EndsWith('/');
            int index = closing ? 1 : 0; SkipSpace(tag, ref index);
            string name = ReadName(tag, ref index).ToLowerInvariant();
            if (name.Length == 0) throw new FormatException("Unsupported HTML tag.");
            var attributes = closing ? new Dictionary<string, string>(StringComparer.Ordinal) : ReadAttributes(tag, ref index, selfClosing);
            if (!closing && (name is "script" or "style"))
            {
                int rawEnd = html.IndexOf("</" + name, position, StringComparison.OrdinalIgnoreCase);
                if (rawEnd < 0) throw new FormatException("Unclosed raw HTML element.");
                position = TagEnd(html, rawEnd + 2) + 1; continue;
            }
            if (name == "main" && !closing)
            {
                if (main != null || closed || selfClosing) throw new FormatException("Ambiguous article main element.");
                main = new(name, attributes, null); stack.Push(main); continue;
            }
            if (stack.Count == 0) continue;
            if (closing)
            {
                SkipSpace(tag, ref index);
                if (index != tag.Length || stack.Peek().Name != name) throw new FormatException("Unbalanced article structure.");
                stack.Pop(); if (name == "main") closed = true;
                continue;
            }
            if (++nodes > 24_000 || stack.Count >= 64) throw new FormatException("Article structure exceeds its bound.");
            var element = new Element(name, attributes, stack.Peek()); stack.Peek().Children.Add(element);
            if (!selfClosing && !VoidTags.Contains(name)) stack.Push(element);
        }
        if (main == null || !closed || stack.Count != 0 || main.Suppressed) throw new FormatException("A complete visible article main element is required.");
        return new(main);
    }
    internal static string Normalize(string text)
    {
        string decoded = WebUtility.HtmlDecode(text);
        var output = new StringBuilder(decoded.Length); bool pendingSpace = false;
        foreach (char character in decoded)
        {
            if (char.IsWhiteSpace(character)) { pendingSpace = output.Length > 0; continue; }
            if (char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format)
                throw new FormatException("Unsupported control or format character in article evidence.");
            if (pendingSpace) output.Append(' ');
            output.Append(character); pendingSpace = false;
        }
        return output.ToString();
    }
    private static int TagEnd(string html, int position)
    {
        char quote = '\0';
        for (int index = position; index < html.Length; index++)
        {
            char value = html[index];
            if (quote != '\0') { if (value == quote) quote = '\0'; continue; }
            if (value is '\'' or '"') quote = value;
            else if (value == '>') return index;
        }
        throw new FormatException("Unclosed HTML tag.");
    }
    private static void SkipSpace(string value, ref int index) { while (index < value.Length && char.IsWhiteSpace(value[index])) index++; }
    private static string ReadName(string value, ref int index)
    {
        int start = index;
        while (index < value.Length && (char.IsAsciiLetterOrDigit(value[index]) || value[index] is '-' or '_' or ':' or '.')) index++;
        return value[start..index];
    }
    private static Dictionary<string, string> ReadAttributes(string tag, ref int index, bool selfClosing)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        while (index < tag.Length)
        {
            SkipSpace(tag, ref index);
            if (index == tag.Length || selfClosing && index == tag.Length - 1 && tag[index] == '/') break;
            string name = ReadName(tag, ref index).ToLowerInvariant();
            if (name.Length == 0 || attributes.Count >= 128) throw new FormatException("Unsupported article attributes.");
            SkipSpace(tag, ref index); string value = "";
            if (index < tag.Length && tag[index] == '=')
            {
                index++; SkipSpace(tag, ref index);
                if (index >= tag.Length) throw new FormatException("Missing attribute value.");
                if (tag[index] is '\'' or '"')
                {
                    char quote = tag[index++]; int start = index;
                    while (index < tag.Length && tag[index] != quote) index++;
                    if (index == tag.Length) throw new FormatException("Unclosed attribute value.");
                    value = tag[start..index++];
                }
                else
                {
                    int start = index; while (index < tag.Length && !char.IsWhiteSpace(tag[index])) index++;
                    value = tag[start..index];
                }
            }
            if (!attributes.TryAdd(name, WebUtility.HtmlDecode(value))) throw new FormatException("Ambiguous duplicate HTML attribute.");
        }
        return attributes;
    }
}
