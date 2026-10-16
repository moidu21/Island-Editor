using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Anno1800IslandTool.UI;

public sealed class LanguageSelectionWindow : Window
{
    private readonly ComboBox _languageComboBox = new();

    public LanguageSelectionWindow(UiLanguage initialLanguage)
    {
        Title = "Language / Sprache / Langue";
        Width = 440;
        Height = 230;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Choisir la langue / Choose language / Sprache wählen"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        _languageComboBox.Height = 30;
        _languageComboBox.Margin = new Thickness(0, 0, 0, 10);
        foreach (var choice in UiLocalizationManager.Choices)
        {
            var suffix = choice.Language == UiLanguage.Auto
                ? $" ({UiLocalizationManager.DetectOperatingSystemLanguage()})"
                : string.Empty;
            _languageComboBox.Items.Add(new ComboBoxItem
            {
                Content = choice.DisplayName + suffix,
                Tag = choice.Language
            });
        }

        var index = UiLocalizationManager.Choices.ToList().FindIndex(c => c.Language == initialLanguage);
        _languageComboBox.SelectedIndex = Math.Max(0, index);
        Grid.SetRow(_languageComboBox, 1);
        root.Children.Add(_languageComboBox);

        var hint = new TextBlock
        {
            Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Auto OS utilise la langue de Windows si elle est prise en charge. Le choix peut ensuite être changé dans la fenêtre principale."),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(hint, 2);
        root.Children.Add(hint);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var ok = new Button
        {
            Content = "OK",
            IsDefault = true,
            Width = 90,
            Height = 30,
            Margin = new Thickness(0, 0, 8, 0)
        };
        ok.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
        var cancel = new Button
        {
            Content = Anno1800IslandTool.Core.Localization.TextCatalog.T("Annuler / Cancel"),
            IsCancel = true,
            Width = 120,
            Height = 30
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        Content = root;
    }

    public UiLanguage SelectedLanguage
        => (_languageComboBox.SelectedItem as ComboBoxItem)?.Tag is UiLanguage language
            ? language
            : UiLanguage.Auto;
}
