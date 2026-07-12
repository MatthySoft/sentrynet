using SentryNet.Models;

namespace SentryNet;

/// <summary>App-wide UI state bound from XAML (e.g. the hide-local-addresses toggle).</summary>
public sealed class AppState : ObservableObject
{
    public static AppState Instance { get; } = new();

    bool _hideLocal;
    /// <summary>When true, endpoints local to this machine (loopback/LAN) are hidden.</summary>
    public bool HideLocal { get => _hideLocal; set => Set(ref _hideLocal, value); }
}
