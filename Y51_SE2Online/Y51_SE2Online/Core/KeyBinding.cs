using System;
using System.Collections.Generic;
using System.Linq;

namespace Y51_SE2Online.Core;

/// <summary>A rebindable hotkey such as "Enter", "F3" or "Alt+F10", stored as Windows virtual-key codes (the game numbers its keyboard inputs the same way).</summary>
public readonly record struct KeyBinding(int Vk, bool Ctrl, bool Shift, bool Alt)
{
    private const int VkShift = 0x10, VkCtrl = 0x11, VkAlt = 0x12;
    private static readonly Dictionary<string, int> Keys = BuildKeys();
    private static readonly Dictionary<int, string> Names = Keys.GroupBy(k => k.Value).ToDictionary(g => g.Key, g => g.First().Key);

    private static Dictionary<string, int> BuildKeys()
    {
        var keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Tab"] = 0x09, ["Space"] = 0x20, ["Backspace"] = 0x08, ["Back"] = 0x08,
            ["CapsLock"] = 0x14, ["Capital"] = 0x14, ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["Prior"] = 0x21,
            ["PageDown"] = 0x22, ["Next"] = 0x22, ["Pause"] = 0x13, ["Tilde"] = 0xC0, ["OemTilde"] = 0xC0, ["Oem3"] = 0xC0,
            ["Apostrophe"] = 0xDE, ["OemQuotes"] = 0xDE, ["Slash"] = 0xBF, ["OemQuestion"] = 0xBF, ["Oem2"] = 0xBF,
            ["Backslash"] = 0xDC, ["OemPipe"] = 0xDC, ["Oem5"] = 0xDC, ["Semicolon"] = 0xBA, ["OemSemicolon"] = 0xBA, ["Oem1"] = 0xBA,
            ["Minus"] = 0xBD, ["OemMinus"] = 0xBD, ["Plus"] = 0xBB, ["OemPlus"] = 0xBB, ["Comma"] = 0xBC, ["OemComma"] = 0xBC,
            ["Period"] = 0xBE, ["OemPeriod"] = 0xBE, ["LeftBracket"] = 0xDB, ["OemOpenBrackets"] = 0xDB, ["RightBracket"] = 0xDD, ["OemCloseBrackets"] = 0xDD
        };
        for (int i = 1; i <= 24; i++) keys["F" + i] = 0x6F + i;
        for (char c = 'A'; c <= 'Z'; c++) keys[c.ToString()] = c;
        for (int i = 0; i <= 9; i++) { keys[i.ToString()] = 0x30 + i; keys["D" + i] = 0x30 + i; keys["NumPad" + i] = 0x60 + i; }
        return keys;
    }

    public static bool TryParse(string text, out KeyBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool ctrl = false, shift = false, alt = false;
        string[] parts = text.Split('+', StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
            case "ctrl": case "control": ctrl = true; break;
            case "shift": shift = true; break;
            case "alt": alt = true; break;
            default: return false;
            }
        }
        if (!Keys.TryGetValue(parts[^1], out int vk)) return false;
        binding = new KeyBinding(vk, ctrl, shift, alt);
        return true;
    }

    /// <summary>Formats a virtual-key code with the held modifiers; null if the key has no name here.</summary>
    public static string Format(int vk, bool ctrl, bool shift, bool alt)
        => Names.ContainsKey(vk) ? new KeyBinding(vk, ctrl, shift, alt).ToString() : null;

    public bool IsDown(Func<int, bool> isDown)
        => Vk != 0 && isDown(Vk) && isDown(VkCtrl) == Ctrl && isDown(VkAlt) == Alt && (!Shift || isDown(VkShift));

    public override string ToString()
        => (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + (Names.TryGetValue(Vk, out var n) ? n : "?");
}
