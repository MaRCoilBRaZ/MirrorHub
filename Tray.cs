namespace MirrorHub;

/// <summary>Platform-neutral description of a notification-area menu item.</summary>
public sealed record TrayItem(
    string Text,
    Action? Click = null,
    bool Checked = false,
    bool Enabled = true,
    bool Bold = false,
    string? Shortcut = null,
    IReadOnlyList<TrayItem>? Children = null)
{
    public static readonly TrayItem Separator = new("-");
}

public interface ITray : IDisposable
{
    bool IsAvailable { get; }
    bool RefreshAvailability();
    event Action? Activated;
    void Invalidate();
    void Notify(string title, string text, Action? onClick);
}
