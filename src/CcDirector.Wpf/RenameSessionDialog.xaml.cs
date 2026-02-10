using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CcDirector.Wpf;

public partial class RenameSessionDialog : Window
{
    private static readonly (string Name, string Hex)[] Presets =
    [
        ("Red",     "#DC2626"),
        ("Orange",  "#EA580C"),
        ("Amber",   "#D97706"),
        ("Green",   "#16A34A"),
        ("Teal",    "#0D9488"),
        ("Blue",    "#2563EB"),
        ("Indigo",  "#4F46E5"),
        ("Purple",  "#7C3AED"),
        ("Pink",    "#DB2777"),
        ("Default", "#252526"),
    ];

    private Border? _selectedSwatch;

    public string? SessionName { get; private set; }
    public string? SelectedColor { get; private set; }

    public RenameSessionDialog(string currentName, string? currentColor = null)
    {
        InitializeComponent();
        NameInput.Text = currentName;
        SelectedColor = currentColor;
        BuildSwatches(currentColor);
        Loaded += (_, _) =>
        {
            NameInput.Focus();
            NameInput.SelectAll();
        };
    }

    private void BuildSwatches(string? currentColor)
    {
        // Normalize null/empty to default
        var normalizedCurrent = string.IsNullOrWhiteSpace(currentColor) ? "#252526" : currentColor;

        foreach (var (name, hex) in Presets)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var fill = new SolidColorBrush(color);
            fill.Freeze();

            bool isSelected = string.Equals(hex, normalizedCurrent, StringComparison.OrdinalIgnoreCase);

            var swatch = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 6, 6),
                Background = fill,
                BorderThickness = new Thickness(2),
                BorderBrush = isSelected ? Brushes.White : Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = name,
                Tag = hex,
            };

            swatch.MouseLeftButtonDown += Swatch_Click;
            ColorSwatchPanel.Children.Add(swatch);

            if (isSelected)
                _selectedSwatch = swatch;
        }
    }

    private void Swatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border clicked) return;

        // Deselect previous
        if (_selectedSwatch != null)
            _selectedSwatch.BorderBrush = Brushes.Transparent;

        // Select new
        clicked.BorderBrush = Brushes.White;
        _selectedSwatch = clicked;
        SelectedColor = clicked.Tag as string;
    }

    private void NameInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Accept();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e) => Accept();

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Accept()
    {
        SessionName = NameInput.Text;
        // Normalize "Default" color to null
        if (string.Equals(SelectedColor, "#252526", StringComparison.OrdinalIgnoreCase))
            SelectedColor = null;
        DialogResult = true;
    }
}
