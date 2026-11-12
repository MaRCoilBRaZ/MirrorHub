namespace MirrorHub;

/// <summary>Global hotkeys are not registered directly on Linux (desktop shortcuts use --command instead).</summary>
sealed class NoHotkeys : IGlobalHotkeys
{
    public bool IsSupported => false;
    public bool Register(string gesture, Action action) => false;
    public void Clear() { }
}
