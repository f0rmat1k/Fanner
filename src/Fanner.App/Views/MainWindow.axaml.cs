using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Fanner.App.ViewModels;

namespace Fanner.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Tunnelling handlers, so the gesture is recorded before the Slider acts on
        // it and the resulting value change is already known to be the user's.
        // A bound property change on its own cannot be trusted as intent: the Slider
        // also writes back values it coerced itself, and acting on those took over
        // GPU fans nobody had touched.
        AddHandler(PointerPressedEvent, OnInputBeforeSlider, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnInputBeforeSlider, RoutingStrategies.Tunnel);

        // Dragging a curve point is a gesture, not a bindable value, so the control
        // reports it and the view model decides what the new shape is.
        CurveCanvas.PointMoved += (_, e) =>
            (DataContext as MainViewModel)?.CurveEditor?.MovePoint(e.Index, e.Point);
    }

    private static void OnInputBeforeSlider(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Visual source
            && source.FindAncestorOfType<Slider>(includeSelf: true) is { DataContext: FanViewModel fan })
        {
            fan.BeginUserAdjust();
        }
    }
}
