using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SE2OnlineInstaller;

/// <summary>
/// Space Engineers 2's Steam launch options: our "-plugins:...Y51_SE2Online.dll" goes in, every other option the player
/// set stays as it was. Paths with spaces are quoted as a whole token, which Windows' argument splitting turns back
/// into one argument for the game.
/// </summary>
public static class LaunchOptions
{
    public const string PluginFile = "Y51_SE2Online.dll";

    public static string Token(string pluginPath) => Quote("-plugins:" + pluginPath);

    /// <summary>The options with our plugin pointing at <paramref name="pluginPath"/> (any older copy of it removed).</summary>
    public static string With(string? existing, string pluginPath) => string.Join(" ", Without(Split(existing)).Append(Token(pluginPath)));

    /// <summary>The options without our plugin.</summary>
    public static string Without(string? existing) => string.Join(" ", Without(Split(existing)));

    /// <summary>The plugin path the options load, or null.</summary>
    public static string? Current(string? existing) => Split(existing).Select(PluginOf).FirstOrDefault(p => p != null);

    private static IEnumerable<string> Without(IEnumerable<string> tokens) => tokens.Where(t => PluginOf(t) == null).Select(Quote);

    private static string? PluginOf(string token) =>
        token.StartsWith("-plugins:", StringComparison.OrdinalIgnoreCase) && token.EndsWith(PluginFile, StringComparison.OrdinalIgnoreCase)
            ? token["-plugins:".Length..] : null;

    private static string Quote(string token) => token.Any(char.IsWhiteSpace) ? "\"" + token + "\"" : token;

    /// <summary>Splits like the Windows command line: whitespace separates, double quotes group (and are removed).</summary>
    public static List<string> Split(string? text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in text ?? "")
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) tokens.Add(current.ToString());
                current.Clear(); any = false;
                continue;
            }
            current.Append(c); any = true;
        }
        if (any) tokens.Add(current.ToString());
        return tokens;
    }
}
