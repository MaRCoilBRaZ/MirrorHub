using Avalonia.Input;

namespace MirrorHub;

public interface IGlobalHotkeys
{
    bool IsSupported { get; }
    bool Register(string gesture, Action action);
    void Clear();
}

/// <summary>Shared gesture parsing and formatting for global keyboard shortcuts.</summary>
static class GlobalHotkeys
{
    /// <summary>Parses "Ctrl+Alt+M"; at least one modifier is required.</summary>
    public static bool TryParse(string gesture, out KeyModifiers mods, out Key key)
    {
        mods = KeyModifiers.None;
        key = Key.None;
        Key? parsed = null;
        foreach (var part in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": mods |= KeyModifiers.Control; break;
                case "alt": mods |= KeyModifiers.Alt; break;
                case "shift": mods |= KeyModifiers.Shift; break;
                case "win": mods |= KeyModifiers.Meta; break;
                default:
                    if (part.Length == 1 && char.IsDigit(part[0])) parsed = Key.D0 + (part[0] - '0');
                    else if (Enum.TryParse<Key>(part, true, out var k) && !int.TryParse(part, out _)) parsed = k;
                    else return false;
                    break;
            }
        }
        if (parsed is not { } p || p == Key.None || mods == KeyModifiers.None) return false;
        key = p;
        return true;
    }

    /// <summary>Turns a key press into "Ctrl+Alt+M"; null for a lone modifier or no modifier.</summary>
    public static string? Format(KeyModifiers mods, Key key)
    {
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
            return null;

        var parts = new List<string>();
        if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        if (parts.Count == 0) return null;
        parts.Add(key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (key - Key.D0))).ToString() : key.ToString());
        return string.Join("+", parts);
    }
}
