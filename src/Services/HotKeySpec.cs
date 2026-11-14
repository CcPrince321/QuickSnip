using System;
using System.Collections.Generic;
using System.Windows.Input;
using QuickSnip.Native;

namespace QuickSnip.Services;

/// <summary>
/// 热键描述：MOD_* 组合 + 虚拟键码。可序列化成 "Ctrl+Alt+A" 这样的字符串存进 settings.json。
/// </summary>
public sealed class HotKeySpec
{
    public HotKeySpec(uint modifiers, uint virtualKey)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
    }

    /// <summary>MOD_CONTROL | MOD_ALT | ...</summary>
    public uint Modifiers { get; }

    public uint VirtualKey { get; }

    public static HotKeySpec Default => new(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x41 /* A */);

    public string ToDisplayString()
    {
        var parts = new List<string>(5);
        if ((Modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((Modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }

    public static string KeyName(uint vk)
    {
        if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
        if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);
        Key k = KeyInterop.KeyFromVirtualKey((int)vk);
        return k == Key.None ? $"0x{vk:X2}" : k.ToString();
    }

    /// <summary>解析失败时回落到默认热键，不让坏配置把软件搞崩。</summary>
    public static HotKeySpec Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Default;

        uint mods = 0;
        uint vk = 0;

        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= NativeMethods.MOD_CONTROL;
                    continue;
                case "alt":
                    mods |= NativeMethods.MOD_ALT;
                    continue;
                case "shift":
                    mods |= NativeMethods.MOD_SHIFT;
                    continue;
                case "win":
                case "windows":
                    mods |= NativeMethods.MOD_WIN;
                    continue;
            }
            vk = ParseKey(raw);
        }

        return vk == 0 ? Default : new HotKeySpec(mods, vk);
    }

    private static uint ParseKey(string token)
    {
        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || "`-=[]\\;',./".IndexOf(c) >= 0)
                return c;
        }

        if (token.Length >= 2 && (token[0] == 'F' || token[0] == 'f')
            && int.TryParse(token.Substring(1), out int n) && n >= 1 && n <= 24)
            return (uint)(0x70 + n - 1);

        if (Enum.TryParse<Key>(token, true, out Key key) && key != Key.None)
        {
            int v = KeyInterop.VirtualKeyFromKey(key);
            if (v > 0) return (uint)v;
        }

        return 0;
    }

    public static uint ToNativeModifiers(ModifierKeys mods)
    {
        uint result = 0;
        if ((mods & ModifierKeys.Control) != 0) result |= NativeMethods.MOD_CONTROL;
        if ((mods & ModifierKeys.Alt) != 0) result |= NativeMethods.MOD_ALT;
        if ((mods & ModifierKeys.Shift) != 0) result |= NativeMethods.MOD_SHIFT;
        if ((mods & ModifierKeys.Windows) != 0) result |= NativeMethods.MOD_WIN;
        return result;
    }

    public bool SameAs(HotKeySpec other) => Modifiers == other.Modifiers && VirtualKey == other.VirtualKey;

    public override string ToString() => ToDisplayString();
}
