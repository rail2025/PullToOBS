using System;
using System.Numerics;
using Dalamud.Configuration;

namespace OBSToABB;

[Serializable]
public class PullToOBSConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string ObsWebSocketUrl { get; set; } = "ws://localhost:4455";

    public string ObsPassword { get; set; } = "";

    public bool AutoConnectOnStart { get; set; } = false;

    public Vector2 IndicatorPosition { get; set; } = new Vector2(300, 300);

    public float IndicatorScale { get; set; } = 1.0f;

    public bool HideIndicator { get; set; } = false;

    /// <summary>
    /// Delegate used to persist this configuration. Injected by the plugin at startup
    /// to avoid static coupling to the plugin interface.
    /// </summary>
    [NonSerialized]
    private Action<IPluginConfiguration>? _saveAction;

    public void SetSaveAction(Action<IPluginConfiguration> saveAction)
    {
        _saveAction = saveAction;
    }

    public void Save()
    {
        _saveAction?.Invoke(this);
    }
}
