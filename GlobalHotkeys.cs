using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace MirrorHub;

/// <summary>System-wide keyboard shortcuts (RegisterHotKey), delivered to a window handle.</summary>
sealed class GlobalHotkeys
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    readonly IntPtr _hwnd;
    readonly Dictionary<int, Action> _actions = new();
    int _nextId = 1;

    public GlobalHotkeys(IntPtr hwnd)
    {
        _hwnd = hwnd;
        HwndSource.FromHwnd(hwnd)!.AddHook(Hook);
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Clear()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_hwnd, id);
        _actions.Clear();
    }

    /// <summary>False when the gesture is invalid or already taken by another application.</summary>
    public bool Register(string gesture, Action action)
    {
        if (!TryParse(gesture, out var mods, out var vk)) return false;
        var id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, mods | MOD_NOREPEAT, vk)) return false;
        _actions[id] = action;
        return true;
    }

    /// <summary>Parses "Ctrl+Alt+M"; at least one modifier is required.</summary>
    public static bool TryParse(string gesture, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        Key? key = null;
        foreach (var part in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": mods |= MOD_WIN; break;
                default:
                    if (part.Length == 1 && char.IsDigit(part[0])) key = Key.D0 + (part[0] - '0');
                    else if (Enum.TryParse<Key>(part, true, out var k) && !int.TryParse(part, out _)) key = k;
                    else return false;
                    break;
            }
        }
        if (key == null || mods == 0) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key.Value);
        return vk != 0;
    }

    /// <summary>Turns a key press into "Ctrl+Alt+M"; null for a lone modifier or no modifier.</summary>
    public static string? Format(ModifierKeys mods, Key key)
    {
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
            return null;

        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (parts.Count == 0) return null;
        parts.Add(key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (key - Key.D0))).ToString() : key.ToString());
        return string.Join("+", parts);
    }
}
