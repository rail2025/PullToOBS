using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace OBSToABB.Windows;

public class OBSStatusIndicator : Window, IDisposable
{
    private readonly PullToOBSPlugin _plugin;
    private readonly IClientState _clientState;
    private readonly ICondition _condition;

    private bool _dragging;
    private Vector2 _dragOffset;

    private const float DotSize = 22.0f;

    /// <summary>
    /// Flags that are always present on this window.
    /// </summary>
    private const ImGuiWindowFlags BaseFlags =
        ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoTitleBar |
        ImGuiWindowFlags.NoResize |
        ImGuiWindowFlags.NoScrollbar |
        ImGuiWindowFlags.NoScrollWithMouse |
        ImGuiWindowFlags.NoBackground |
        ImGuiWindowFlags.NoFocusOnAppearing |
        ImGuiWindowFlags.NoBringToFrontOnFocus;

    public OBSStatusIndicator(PullToOBSPlugin plugin, IClientState clientState, ICondition condition)
        : base("###PullToOBSIndicator", BaseFlags)
    {
        _plugin = plugin;
        _clientState = clientState;
        _condition = condition;

        // Keep the window permanently open; visibility is controlled via DrawConditions.
        IsOpen = true;

        // Disable Dalamud's built-in close button / collapse behavior.
        RespectCloseHotkey = false;
    }

    public void Dispose() { }

    private bool IsInValidGameplayState()
    {
        if (!_clientState.IsLoggedIn)
            return false;

        if (_clientState.IsGPosing)
            return false;

        if (_condition[ConditionFlag.WatchingCutscene] ||
            _condition[ConditionFlag.WatchingCutscene78] ||
            _condition[ConditionFlag.OccupiedInCutSceneEvent])
            return false;

        if (_condition[ConditionFlag.BetweenAreas] ||
            _condition[ConditionFlag.BetweenAreas51])
            return false;

        if (_condition[ConditionFlag.CreatingCharacter])
            return false;

        return true;
    }

    /// <summary>
    /// Controls whether the window is drawn this frame.
    /// Returning false skips PreDraw, Draw, and PostDraw entirely.
    /// </summary>
    public override bool DrawConditions()
    {
        if (_plugin.Configuration.HideIndicator)
            return false;

        var unlocked = _plugin.ConfigWindow.IsOpen;
        return IsInValidGameplayState() || unlocked;
    }

    /// <summary>
    /// Called before each Draw frame. Sets position, size, style, and updates Flags.
    /// Only runs when DrawConditions returns true.
    /// </summary>
    public override void PreDraw()
    {
        // Toggle NoInputs depending on whether the config window is open (unlock mode).
        var unlocked = _plugin.ConfigWindow.IsOpen;
        Flags = unlocked ? BaseFlags : BaseFlags | ImGuiWindowFlags.NoInputs;

        var scale = _plugin.Configuration.IndicatorScale;
        var pos = _plugin.Configuration.IndicatorPosition;
        var size = new Vector2(100 * scale, 40 * scale);

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10.0f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
    }

    /// <summary>
    /// Called after each Draw frame. Pops style vars pushed in PreDraw.
    /// </summary>
    public override void PostDraw()
    {
        ImGui.PopStyleVar(2);
    }

    /// <summary>
    /// Draws the indicator content. Dalamud handles Begin/End.
    /// </summary>
    public override void Draw()
    {
        var scale = _plugin.Configuration.IndicatorScale;
        var unlocked = _plugin.ConfigWindow.IsOpen;

        var drawList = ImGui.GetWindowDrawList();
        var winPos = ImGui.GetWindowPos();
        var winSize = ImGui.GetWindowSize();

        drawList.PushClipRect(winPos, winPos + winSize, true);

        // Background
        var rounding = 5.0f * scale;
        var bgColor = ImGui.GetColorU32(new Vector4(0.1f, 0.1f, 0.1f, 0.9f));
        drawList.AddRectFilled(winPos, winPos + winSize, bgColor, rounding);

        // Text (left side, using scaled font)
        using var font = _plugin.IndicatorFont.Available ? _plugin.IndicatorFont.Push() : null;

        const string text = "OBS";
        var textSize = ImGui.CalcTextSize(text);
        var textColorU32 = ImGui.GetColorU32(new Vector4(0.95f, 0.95f, 0.95f, 1.0f));
        var textPos = new Vector2(
            winPos.X + 10.0f * scale,
            winPos.Y + (winSize.Y - textSize.Y) / 2.0f);

        drawList.AddText(textPos, textColorU32, text);

        // Dot (right of text)
        var dotSize = DotSize * scale;
        var dotCenter = new Vector2(
            winPos.X + winSize.X - (10.0f * scale) - dotSize / 2.0f,
            winPos.Y + winSize.Y / 2.0f);
        drawList.AddCircleFilled(dotCenter, dotSize / 2.0f, ImGui.GetColorU32(GetStatusColor()), 32);

        // Corner accents
        DrawCornerAccents(drawList, winPos, winSize, rounding, scale);

        drawList.PopClipRect();

        // Dragging
        if (unlocked)
        {
            var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);

            if (!_dragging && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _dragging = true;
                _dragOffset = ImGui.GetMousePos() - winPos;
            }

            if (_dragging)
            {
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    var newPos = ImGui.GetMousePos() - _dragOffset;
                    ImGui.SetWindowPos(newPos);
                    _plugin.Configuration.IndicatorPosition = newPos;
                }
                else
                {
                    _dragging = false;
                    _plugin.Configuration.Save();
                }
            }
        }
        else
        {
            _dragging = false;
        }
    }

    private Vector4 GetStatusColor()
    {
        var obs = _plugin.ObsController;

        if (obs.IsRecording)
            return GetPulsingRedColor();

        if (obs.IsConnected && _plugin.EncounterManager.IsStandby)
            return new Vector4(0.0f, 1.0f, 0.0f, 1.0f);

        if (obs.IsReplayBufferActive)
            return new Vector4(1.0f, 0.6f, 0.0f, 1.0f);

        if (obs.IsConnected)
            return new Vector4(0.0f, 1.0f, 0.0f, 1.0f);

        return new Vector4(0.5f, 0.5f, 0.5f, 1.0f);
    }

    private static Vector4 GetPulsingRedColor()
    {
        var time = (float)ImGui.GetTime();
        var pulse = (MathF.Sin(time * MathF.PI) + 1.0f) / 2.0f;
        var alpha = 0.4f + 0.6f * pulse;
        return new Vector4(1.0f, 0.0f, 0.0f, alpha);
    }

    private static void DrawCornerAccents(ImDrawListPtr drawList, Vector2 winPos, Vector2 winSize, float rounding, float scale)
    {
        var color = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.9f));
        var thickness = 2.0f * scale;
        var len = 8.0f * scale;
        var half = thickness * 0.5f;

        var p0 = winPos + new Vector2(half, half);
        var p1 = winPos + winSize - new Vector2(half, half);

        var x0 = p0.X; var y0 = p0.Y;
        var x1 = p1.X; var y1 = p1.Y;

        var r = MathF.Max(0.0f, rounding - half);

        // Top-left
        drawList.PathClear();
        drawList.PathLineTo(new Vector2(x0, y0 + r + len));
        drawList.PathLineTo(new Vector2(x0, y0 + r));
        drawList.PathArcTo(new Vector2(x0 + r, y0 + r), r, MathF.PI, 1.5f * MathF.PI);
        drawList.PathLineTo(new Vector2(x0 + r + len, y0));
        drawList.PathStroke(color, ImDrawFlags.None, thickness);

        // Top-right
        drawList.PathClear();
        drawList.PathLineTo(new Vector2(x1 - r - len, y0));
        drawList.PathLineTo(new Vector2(x1 - r, y0));
        drawList.PathArcTo(new Vector2(x1 - r, y0 + r), r, 1.5f * MathF.PI, 2.0f * MathF.PI);
        drawList.PathLineTo(new Vector2(x1, y0 + r + len));
        drawList.PathStroke(color, ImDrawFlags.None, thickness);

        // Bottom-right
        drawList.PathClear();
        drawList.PathLineTo(new Vector2(x1, y1 - r - len));
        drawList.PathLineTo(new Vector2(x1, y1 - r));
        drawList.PathArcTo(new Vector2(x1 - r, y1 - r), r, 0.0f, 0.5f * MathF.PI);
        drawList.PathLineTo(new Vector2(x1 - r - len, y1));
        drawList.PathStroke(color, ImDrawFlags.None, thickness);

        // Bottom-left
        drawList.PathClear();
        drawList.PathLineTo(new Vector2(x0 + r + len, y1));
        drawList.PathLineTo(new Vector2(x0 + r, y1));
        drawList.PathArcTo(new Vector2(x0 + r, y1 - r), r, 0.5f * MathF.PI, MathF.PI);
        drawList.PathLineTo(new Vector2(x0, y1 - r - len));
        drawList.PathStroke(color, ImDrawFlags.None, thickness);
    }
}
