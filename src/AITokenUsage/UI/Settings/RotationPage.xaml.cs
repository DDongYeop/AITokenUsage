using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using AITokenUsage.Core.Settings;
using AITokenUsage.Resources;

namespace AITokenUsage.UI.Settings;

/// <summary>"Rotation" page: auto-rotate, interval, pause on hover, skip unavailable AIs (v1.1 spec §4.3).</summary>
public partial class RotationPage : SettingsPage
{
    /// <summary>Slider drags are applied (and saved) once the value has been still for this long.</summary>
    private static readonly TimeSpan SliderApplyDelay = TimeSpan.FromMilliseconds(300);

    private readonly DispatcherTimer _sliderTimer;

    public RotationPage(AppHost host)
        : base(host)
    {
        InitializeComponent();

        _sliderTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = SliderApplyDelay };
        _sliderTimer.Tick += OnSliderTimer;

        HookToggle(RotationEnabledBox, enabled =>
        {
            IntervalCard.IsEnabled = enabled;
            Update(s => s.RotationEnabled = enabled);
        });
        IntervalSlider.ValueChanged += OnIntervalChanged;
        HookToggle(PauseOnHoverBox, on => Update(s => s.PauseOnHover = on));
        HookToggle(SkipUnavailableBox, on => Update(s => s.SkipUnavailable = on));
    }

    protected override void OnLoad()
    {
        AppSettings settings = Host.Settings;

        RotationEnabledBox.IsChecked = settings.RotationEnabled;
        IntervalSlider.Value = settings.RotationSeconds;
        IntervalText.Text = FormatSeconds(settings.RotationSeconds);
        IntervalCard.IsEnabled = settings.RotationEnabled;
        PauseOnHoverBox.IsChecked = settings.PauseOnHover;
        SkipUnavailableBox.IsChecked = settings.SkipUnavailable;
    }

    /// <summary>
    /// Applies a pending slider change synchronously. The window disposes its pages when it closes, and AppHost.Shutdown
    /// closes the window before it starts shutting down, so this change still reaches the final save.
    /// </summary>
    protected override void OnDispose()
    {
        if (_sliderTimer.IsEnabled)
        {
            OnSliderTimer(this, EventArgs.Empty);
        }

        _sliderTimer.Stop();
        _sliderTimer.Tick -= OnSliderTimer;
    }

    private void OnIntervalChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        IntervalText.Text = FormatSeconds((int)Math.Round(e.NewValue));
        if (IsLoading)
        {
            return;
        }

        _sliderTimer.Stop();
        _sliderTimer.Start();
    }

    private void OnSliderTimer(object? sender, EventArgs e)
    {
        _sliderTimer.Stop();
        int seconds = (int)Math.Round(IntervalSlider.Value);
        Update(s => s.RotationSeconds = seconds);
    }

    private static string FormatSeconds(int seconds) =>
        string.Format(CultureInfo.CurrentCulture, UiStrings.RotationSecondsFormat, seconds);
}
