namespace Y51_SE2Online.Core;

/// <summary>The character a Windows virtual-key code types on a US keyboard layout, for platforms whose game input delivers no text.</summary>
public static class KeyText
{
    // ponytail: US layout only; other layouts get US punctuation. Upgrade path is a platform text API (SDL text input).
    public static readonly int[] PrintableKeys = Build();

    private static int[] Build()
    {
        var keys = new System.Collections.Generic.List<int> { 0x20 };
        for (int vk = 0x30; vk <= 0x39; vk++) keys.Add(vk);
        for (int vk = 0x41; vk <= 0x5A; vk++) keys.Add(vk);
        for (int vk = 0x60; vk <= 0x6F; vk++) if (vk != 0x6C) keys.Add(vk);
        keys.AddRange(new[] { 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE });
        return keys.ToArray();
    }

    public static char? CharOf(int vk, bool shift) => vk switch
    {
        0x20 => ' ',
        >= 0x30 and <= 0x39 => shift ? ")!@#$%^&*("[vk - 0x30] : (char)vk,
        >= 0x41 and <= 0x5A => shift ? (char)vk : (char)(vk + 32),
        >= 0x60 and <= 0x69 => (char)('0' + vk - 0x60),
        0x6A => '*', 0x6B => '+', 0x6D => '-', 0x6E => '.', 0x6F => '/',
        0xBA => shift ? ':' : ';', 0xBB => shift ? '+' : '=', 0xBC => shift ? '<' : ',', 0xBD => shift ? '_' : '-',
        0xBE => shift ? '>' : '.', 0xBF => shift ? '?' : '/', 0xC0 => shift ? '~' : '`', 0xDB => shift ? '{' : '[',
        0xDC => shift ? '|' : '\\', 0xDD => shift ? '}' : ']', 0xDE => shift ? '"' : '\'',
        _ => null
    };
}
