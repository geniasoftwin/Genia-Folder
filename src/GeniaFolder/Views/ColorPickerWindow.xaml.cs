using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GeniaFolder.Models;
using GeniaFolder.Services;

namespace GeniaFolder.Views;

public partial class ColorPickerWindow : Window
{
    public FolderColor SelectedColor { get; private set; }

    public ColorPickerWindow(FolderColor current)
    {
        InitializeComponent();

        foreach (var value in Enum.GetValues<FolderColor>())
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
            panel.Children.Add(new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(FolderAppearanceService.GetColor(value)),
                Margin = new Thickness(0, 0, 10, 0)
            });
            panel.Children.Add(new TextBlock { Text = DisplayName(value), VerticalAlignment = VerticalAlignment.Center });

            var item = new ListBoxItem { Content = panel, Tag = value };
            ColorList.Items.Add(item);
            if (value == current)
                ColorList.SelectedItem = item;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (ColorList.SelectedItem is not ListBoxItem item || item.Tag is not FolderColor color)
            return;

        SelectedColor = color;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static string DisplayName(FolderColor color) => color switch
    {
        FolderColor.Blue => "Синий",
        FolderColor.Green => "Зелёный",
        FolderColor.Yellow => "Жёлтый",
        FolderColor.Orange => "Оранжевый",
        FolderColor.Red => "Красный",
        FolderColor.Purple => "Фиолетовый",
        _ => "Серый"
    };
}
