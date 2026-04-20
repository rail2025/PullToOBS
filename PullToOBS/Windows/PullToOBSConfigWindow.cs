using System;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace OBSToABB.Windows;

public class PullToOBSConfigWindow : Window, IDisposable
{
    private readonly PullToOBSPlugin _plugin;
    private readonly PullToOBSConfiguration _configuration;
    private readonly IChatGui _chatGui;

    private string _urlBuffer;
    private string _passwordBuffer;
    private bool _isConnecting;
    private string _statusMessage = "";
    private Vector4 _statusColor = new Vector4(0.5f, 0.5f, 0.5f, 1.0f);

    public PullToOBSConfigWindow(PullToOBSPlugin plugin, IChatGui chatGui) : base("OBSToABB Configuration")
    {
        _plugin = plugin;
        _configuration = plugin.Configuration;
        _chatGui = chatGui;

        Size = new Vector2(500, 400);
        SizeCondition = ImGuiCond.FirstUseEver;

        _urlBuffer = _configuration.ObsWebSocketUrl;
        _passwordBuffer = _configuration.ObsPassword;
    }

    public override void Draw()
    {
        UpdateStatus();

        ImGui.Text("OBS WebSocket Connection");
        ImGui.Separator();
        ImGui.Spacing();

        // URL Input -- save only when user finishes editing (focus loss / Enter)
        ImGui.Text("WebSocket URL:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##Url", ref _urlBuffer, 500);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _configuration.ObsWebSocketUrl = _urlBuffer;
            _configuration.Save();
        }
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "Default: ws://localhost:4455 (OBS WebSocket v5)");

        ImGui.Spacing();

        // Password Input -- save only when user finishes editing
        ImGui.Text("Password:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##Password", ref _passwordBuffer, 500, ImGuiInputTextFlags.Password);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _configuration.ObsPassword = _passwordBuffer;
            _configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Auto-connect checkbox
        var autoConnect = _configuration.AutoConnectOnStart;
        if (ImGui.Checkbox("Auto-connect to OBS on plugin start", ref autoConnect))
        {
            _configuration.AutoConnectOnStart = autoConnect;
            _configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Connection status
        ImGui.Text("Status:");
        ImGui.SameLine();
        ImGui.TextColored(_statusColor, _statusMessage);

        ImGui.Spacing();

        // Connect/Disconnect button
        var obs = _plugin.ObsController;
        var buttonText = obs.IsConnected ? "Disconnect" : "Connect";

        if (_isConnecting) ImGui.BeginDisabled();

        if (ImGui.Button(_isConnecting ? "Connecting..." : buttonText, new Vector2(120, 0)) && !_isConnecting)
        {
            _ = HandleConnectionAsync();
        }

        if (_isConnecting) ImGui.EndDisabled();

        ImGui.SameLine();

        if (ImGui.Button("Close", new Vector2(120, 0)))
        {
            IsOpen = false;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Replay buffer warning
        if (obs.IsConnected && !obs.IsReplayBufferConfigured)
        {
            ImGui.TextColored(
                new Vector4(1.0f, 0.6f, 0.0f, 1.0f),
                "Warning: Please configure Replay Buffer in OBS (Settings > Output > Replay Buffer)");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Indicator settings
        ImGui.Text("Indicator Settings");
        ImGui.Spacing();

        var scale = _configuration.IndicatorScale;
        if (ImGui.SliderFloat("Indicator Scale", ref scale, 0.5f, 2.0f, "%.1fx"))
        {
            _configuration.IndicatorScale = scale;
            _configuration.Save();
        }

        ImGui.Spacing();

        var hideIndicator = _configuration.HideIndicator;
        if (ImGui.Checkbox("Hide Indicator", ref hideIndicator))
        {
            _configuration.HideIndicator = hideIndicator;
            _configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Instructions
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "How it works:");
        ImGui.TextWrapped(
            "When configured and started, the plugin will:" +
            "\n1. Connect to OBS and start the Replay Buffer" +
            "\n2. Detect combat state changes via Dalamud" +
            "\n3. On encounter start: Start recording, wait 5s, save replay buffer" +
            "\n4. On encounter end: Wait 5s overlap, stop recording" +
            "\n\nResult: Two files per encounter — replay buffer clip (prepull) + full recording.");
    }

    private void UpdateStatus()
    {
        var obs = _plugin.ObsController;

        if (_isConnecting)
        {
            _statusMessage = "Connecting...";
            _statusColor = new Vector4(0.8f, 0.8f, 0.0f, 1.0f);
        }
        else if (obs.IsRecording)
        {
            _statusMessage = "Recording";
            _statusColor = new Vector4(1.0f, 0.0f, 0.0f, 1.0f);
        }
        else if (obs.IsConnected && _plugin.EncounterManager.IsStandby)
        {
            _statusMessage = "Standby";
            _statusColor = new Vector4(0.0f, 1.0f, 0.0f, 1.0f);
        }
        else if (obs.IsReplayBufferActive)
        {
            _statusMessage = "Replay Buffer Active";
            _statusColor = new Vector4(1.0f, 0.6f, 0.0f, 1.0f);
        }
        else if (obs.IsConnected)
        {
            _statusMessage = "Connected";
            _statusColor = new Vector4(0.0f, 1.0f, 0.0f, 1.0f);
        }
        else
        {
            _statusMessage = "Not Connected";
            _statusColor = new Vector4(0.5f, 0.5f, 0.5f, 1.0f);
        }
    }

    private async Task HandleConnectionAsync()
    {
        var obs = _plugin.ObsController;

        if (obs.IsConnected)
        {
            // Disconnect
            obs.Disconnect();
            _chatGui.Print("[OBSToABB] Disconnected from OBS");
        }
        else
        {
            // Connect
            _isConnecting = true;

            try
            {
                await obs.ConnectAsync(_configuration.ObsWebSocketUrl, _configuration.ObsPassword);
                _chatGui.Print("[OBSToABB] Connected to OBS");
            }
            catch (Exception ex)
            {
                _chatGui.Print($"[OBSToABB] Failed to connect to OBS: {ex.Message}");
            }
            finally
            {
                _isConnecting = false;
            }
        }
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}
