using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Builder;

public partial class SettingsDialog : Window
{
    public string BackgroundColorHex { get; set; } = "#1E1E1E";
    public string AccentColorHex { get; set; } = "#4FC3F7";

    public Action<string, string>? OnThemeChanged { get; set; }

    private bool _suppressTextChanged;

    public SettingsDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _suppressTextChanged = true;
            BgHexTextBox.Text = BackgroundColorHex;
            AccentHexTextBox.Text = AccentColorHex;
            _suppressTextChanged = false;

            UpdatePreview(BgColorPreview, BackgroundColorHex);
            UpdatePreview(AccentColorPreview, AccentColorHex);
        };
    }

    private void BgSample_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string hex)
        {
            _suppressTextChanged = true;
            BgHexTextBox.Text = hex;
            _suppressTextChanged = false;

            BackgroundColorHex = hex;
            UpdatePreview(BgColorPreview, hex);
            OnThemeChanged?.Invoke(BackgroundColorHex, AccentColorHex);
        }
    }

    private void AccentSample_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string hex)
        {
            _suppressTextChanged = true;
            AccentHexTextBox.Text = hex;
            _suppressTextChanged = false;

            AccentColorHex = hex;
            UpdatePreview(AccentColorPreview, hex);
            OnThemeChanged?.Invoke(BackgroundColorHex, AccentColorHex);
        }
    }

    private void BgHexTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressTextChanged || BgHexTextBox == null) return;
        var hex = BgHexTextBox.Text.Trim();
        if (TryParseColor(hex))
        {
            BackgroundColorHex = hex;
            UpdatePreview(BgColorPreview, hex);
            OnThemeChanged?.Invoke(BackgroundColorHex, AccentColorHex);
        }
    }

    private void AccentHexTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressTextChanged || AccentHexTextBox == null) return;
        var hex = AccentHexTextBox.Text.Trim();
        if (TryParseColor(hex))
        {
            AccentColorHex = hex;
            UpdatePreview(AccentColorPreview, hex);
            OnThemeChanged?.Invoke(BackgroundColorHex, AccentColorHex);
        }
    }

    private void CloseCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        Close();
    }

    private static void UpdatePreview(System.Windows.Controls.Border border, string hex)
    {
        try
        {
            var h = hex.StartsWith('#') ? hex : "#" + hex;
            border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h));
        }
        catch { }
    }

    private static bool TryParseColor(string hex)
    {
        try
        {
            var h = hex.StartsWith('#') ? hex : "#" + hex;
            ColorConverter.ConvertFromString(h);
            return true;
        }
        catch { return false; }
    }
}
