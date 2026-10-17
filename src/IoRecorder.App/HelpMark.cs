using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace IoRecorder.App;

// A small "?" next to an item. Its explanation opens on hover, when it gets keyboard focus (Tab) and on click, and stays
// open while the pointer is on it.
public sealed class HelpMark : Button
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(HelpMark),
        new PropertyMetadata("", (d, e) => { var mark = (HelpMark)d; mark._tip.Content = e.NewValue; AutomationProperties.SetHelpText(mark, (string)e.NewValue); }));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    private readonly ToolTip _tip = new() { Placement = PlacementMode.Bottom };

    public HelpMark()
    {
        Style = (Style)Application.Current.FindResource("HelpMark");
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 150);
        ToolTipService.SetShowDuration(this, int.MaxValue);
        ToolTipService.SetShowsToolTipOnKeyboardFocus(this, true);
        AutomationProperties.SetName(this, "説明");
        Click += (s, e) => { _tip.PlacementTarget = this; _tip.IsOpen = !_tip.IsOpen; };
        LostKeyboardFocus += (s, e) => _tip.IsOpen = false;
    }

    // For the smoke test: the open explanation, rendered on its own.
    internal ToolTip Open() { _tip.PlacementTarget = this; _tip.IsOpen = true; return _tip; }
}
