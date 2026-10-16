using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SE2OnlineInstaller;

/// <summary>
/// Valve's text KeyValues format (localconfig.vdf, libraryfolders.vdf, loginusers.vdf, *.acf): quoted keys, quoted
/// values or { } blocks, backslash escapes, // comments. Order is kept so a rewritten file differs only where we changed it.
/// </summary>
public sealed class KvNode
{
    public string Key { get; set; }
    public string? Value { get; set; }
    public List<KvNode> Children { get; } = new();

    public KvNode(string key, string? value = null) { Key = key; Value = value; }

    public bool IsBlock => Value == null;

    /// <summary>The first child with this key, ignoring case (Steam writes both "apps" and "Apps").</summary>
    public KvNode? Get(string key) => Children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    public string? Text(string key) => Get(key)?.Value;

    /// <summary>The child block with this key, created at the end when missing.</summary>
    public KvNode Block(string key)
    {
        var node = Get(key);
        if (node is { IsBlock: true }) return node;
        if (node != null) Children.Remove(node);
        node = new KvNode(key);
        Children.Add(node);
        return node;
    }

    public void Set(string key, string value)
    {
        var node = Get(key);
        if (node == null) Children.Add(new KvNode(key, value));
        else { node.Value = value; node.Children.Clear(); }
    }

    public static KvNode Parse(string text)
    {
        var tokens = Tokenize(text).GetEnumerator();
        var root = new KvNode("");
        ReadBlock(tokens, root, top: true);
        if (root.Children.Count != 1 || !root.Children[0].IsBlock) throw new FormatException("expected one top-level block");
        return root.Children[0];
    }

    private static void ReadBlock(IEnumerator<(string Text, bool Quoted)> tokens, KvNode parent, bool top)
    {
        while (tokens.MoveNext())
        {
            var (key, quoted) = tokens.Current;
            if (!quoted && key == "}")
            {
                if (top) throw new FormatException("unexpected }");
                return;
            }
            if (!quoted && key == "{") throw new FormatException("block without a key");
            if (!tokens.MoveNext()) throw new FormatException("key '" + key + "' has no value");
            var (value, valueQuoted) = tokens.Current;
            if (!valueQuoted && value == "{")
            {
                var block = new KvNode(key);
                ReadBlock(tokens, block, top: false);
                parent.Children.Add(block);
            }
            else if (!valueQuoted && value == "}") throw new FormatException("key '" + key + "' has no value");
            else parent.Children.Add(new KvNode(key, value));
        }
        if (!top) throw new FormatException("missing }");
    }

    private static IEnumerable<(string, bool)> Tokenize(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c is '{' or '}') { i++; yield return (c.ToString(), false); continue; }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= text.Length) throw new FormatException("unterminated string");
                    char d = text[i++];
                    if (d == '"') break;
                    if (d == '\\' && i < text.Length)
                    {
                        char e = text[i++];
                        sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => e });
                    }
                    else sb.Append(d);
                }
                yield return (sb.ToString(), true);
                continue;
            }
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"')) i++;
            yield return (text.Substring(start, i - start), false);
        }
    }

    /// <summary>Steam's own layout: tab indents, a tab pair between key and value.</summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        Write(sb, this, 0);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, KvNode node, int depth)
    {
        string indent = new('\t', depth);
        if (!node.IsBlock) { sb.Append(indent).Append(Quote(node.Key)).Append("\t\t").Append(Quote(node.Value!)).Append('\n'); return; }
        sb.Append(indent).Append(Quote(node.Key)).Append('\n').Append(indent).Append("{\n");
        foreach (var child in node.Children) Write(sb, child, depth + 1);
        sb.Append(indent).Append("}\n");
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
}
