using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Win32.Input;
using Forms = System.Windows.Forms;

namespace MirrorHub;

/// <summary>System-wide keyboard shortcuts (RegisterHotKey) via a message-only NativeWindow.</summary>
sealed class WinHotkeys : Forms.NativeWindow, IGlobalHotkeys, IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    readonly Dictionary<int, Action> _actions = new();
    int _nextId = 1;

    public bool IsSupported => true;

    public WinHotkeys()
    {
        CreateHandle(new Forms.CreateParams { Parent = HWND_MESSAGE });
    }

    protected override void WndProc(ref Forms.Message m)
    {
        if (m.Msg == WM_HOTKEY && _actions.TryGetValue(m.WParam.ToInt32(), out var action))
        {
            action();
            return;
        }
        base.WndProc(ref m);
    }

    public void Clear()
    {
        if (Handle == IntPtr.Zero) return;
        foreach (var id in _actions.Keys) UnregisterHotKey(Handle, id);
        _actions.Clear();
    }

    public bool Register(string gesture, Action action)
    {
        if (Handle == IntPtr.Zero || !GlobalHotkeys.TryParse(gesture, out var mods, out var key)) return false;
        var winMods = 0u;
        if (mods.HasFlag(KeyModifiers.Control)) winMods |= MOD_CONTROL;
        if (mods.HasFlag(KeyModifiers.Alt)) winMods |= MOD_ALT;
        if (mods.HasFlag(KeyModifiers.Shift)) winMods |= MOD_SHIFT;
        if (mods.HasFlag(KeyModifiers.Meta)) winMods |= MOD_WIN;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0 || winMods == 0) return false;

        var id = _nextId++;
        if (!RegisterHotKey(Handle, id, winMods | MOD_NOREPEAT, vk)) return false;
        _actions[id] = action;
        return true;
    }

    public void Dispose()
    {
        Clear();
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}
