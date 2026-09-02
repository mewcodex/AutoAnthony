using System.Diagnostics;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;

namespace AutoAnthony;

/// <summary>
/// Loading overlay shared by generated-pool assembly and the remaining vanilla run-start pipeline.
/// </summary>
internal sealed class ChaosGenerationProgressOverlay : IDisposable
{
    private static readonly StringName FontSize = new("font_size");
    private static readonly StringName FontColor = new("font_color");
    private static readonly StringName OutlineColor = new("font_outline_color");
    private static readonly StringName OutlineSize = new("outline_size");

    private readonly int _total;
    private CanvasLayer? _layer;
    private Control? _host;
    private Label? _label;
    private long _lastDrawTimestamp;
    private bool _disposed;

    private ChaosGenerationProgressOverlay(int total)
    {
        _total = Math.Max(1, total);
        try
        {
            if (Engine.GetMainLoop() is not SceneTree { Root: { } root }) return;

            // A new run is generated only after NTransition has finished fading the screen to black. A separate
            // root CanvasLayer is not reliably composited above that transition on every renderer. Make the
            // overlay the transition's last, high-Z child so it is drawn on the black loading screen itself.
            // Keep the root CanvasLayer path for unusual entry points where NGame is not ready yet.
            var transition = NGame.Instance?.Transition;
            if (transition?.IsInsideTree() == true && transition.IsVisibleInTree())
            {
                _host = CreateHost();
                transition.AddChild(_host);
                transition.MoveChild(_host, transition.GetChildCount() - 1);
                Log.Info($"[AutoAnthony] Card-pool generation progress attached to GameTransitionRect ({_total} cards).");
            }
            else
            {
                _layer = new CanvasLayer
                {
                    Name = "AutoAnthonyGenerationProgressLayer",
                    Layer = 10_000
                };
                _host = CreateHost();
                _layer.AddChild(_host);
                root.AddChild(_layer);
                Log.Info($"[AutoAnthony] Card-pool generation progress attached to the root viewport ({_total} cards).");
            }

            var backdrop = new ColorRect
            {
                Color = new Color(0f, 0f, 0f, 0.18f),
                MouseFilter = Control.MouseFilterEnum.Stop
            };
            backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect,
                Control.LayoutPresetMode.Minsize, 0);

            _label = new Label
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            _label.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect,
                Control.LayoutPresetMode.Minsize, 0);
            _label.AddThemeFontSizeOverride(FontSize, 34);
            _label.AddThemeColorOverride(FontColor, Colors.White);
            _label.AddThemeColorOverride(OutlineColor, Colors.Black);
            _label.AddThemeConstantOverride(OutlineSize, 8);

            _host.AddChild(backdrop);
            _host.AddChild(_label);
        }
        catch (Exception exception)
        {
            Log.Warn($"[AutoAnthony] Could not create card-pool generation progress overlay: {exception.Message}");
            SafeRelease();
        }
    }

    private static Control CreateHost()
    {
        var host = new Control
        {
            Name = "AutoAnthonyGenerationProgress",
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZAsRelative = false,
            ZIndex = 4_096
        };
        host.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect,
            Control.LayoutPresetMode.Minsize, 0);
        return host;
    }

    internal static ChaosGenerationProgressOverlay Create(int total) => new(total);

    internal void Report(int current)
    {
        if (_disposed || _label is null) return;
        current = Math.Clamp(current, 1, _total);
        var chinese = LocManager.Instance?.Language is "zhs" or "zht";
        _label.Text = chinese
            ? $"正在生成卡池中……（{current}/{_total}）"
            : $"Generating card pools... ({current}/{_total})";

        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _lastDrawTimestamp) / (double)Stopwatch.Frequency;
        if (current != 1 && current != _total && elapsed < 1d / 30d) return;
        _lastDrawTimestamp = now;
        ForceDrawSafely();
    }

    internal void ShowEnteringRun()
    {
        if (_disposed || _label is null) return;
        var chinese = LocManager.Instance?.Language is "zhs" or "zht";
        _label.Text = chinese ? "正在进入游戏……" : "Entering the run...";
        _lastDrawTimestamp = Stopwatch.GetTimestamp();
        ForceDrawSafely();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_layer is not null) _layer.Visible = false;
        ForceDrawSafely();
        SafeRelease();
    }

    private void SafeRelease()
    {
        try
        {
            if (_host?.IsInsideTree() == true) _host.QueueFree();
            else _host?.Free();
            if (_layer?.IsInsideTree() == true) _layer.QueueFree();
            else _layer?.Free();
        }
        catch
        {
            // The scene may already be tearing down after a failed run start.
        }
        _label = null;
        _host = null;
        _layer = null;
    }

    private static void ForceDrawSafely()
    {
        try
        {
            RenderingServer.ForceDraw(true, 0d);
        }
        catch (Exception exception)
        {
            Log.Warn($"[AutoAnthony] Could not redraw card-pool generation progress: {exception.Message}");
        }
    }
}
