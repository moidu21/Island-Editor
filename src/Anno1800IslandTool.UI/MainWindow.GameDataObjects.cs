using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace Anno1800IslandTool.UI;

public partial class MainWindow
{
    private readonly ObservableCollection<EditableGameDataAreaObjectRow> _gameDataObjectRows = new();
    private readonly ObservableCollection<string> _gameDataObjectContainerLabels = new();
    private GameDataAreaObjectsSnapshot? _gameDataObjectSnapshot;

    private void InitializeGameDataObjectsEditor()
    {
        GameDataObjectsDataGrid.ItemsSource = _gameDataObjectRows;
        GameDataObjectContainerComboBox.ItemsSource = _gameDataObjectContainerLabels;
        GameDataObjectsStatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucun projet chargé.");
    }

    private void RefreshGameDataObjectsEditor()
    {
        _gameDataObjectRows.Clear();
        _gameDataObjectContainerLabels.Clear();
        _gameDataObjectSnapshot = null;

        if (_currentProject is null || string.IsNullOrWhiteSpace(_currentProject.RawContent.GameDataText))
        {
            GameDataObjectsStatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucun gamedata.xml chargé.");
            return;
        }

        try
        {
            _gameDataObjectSnapshot = GameDataAreaObjectsXmlEditor.Extract(_currentProject.RawContent.GameDataText!);
            foreach (var container in _gameDataObjectSnapshot.Containers)
            {
                _gameDataObjectContainerLabels.Add(Anno1800IslandTool.Core.Localization.TextCatalog.F($"{container.Index} - AreaManagerData#{container.AreaManagerDataIndex} / AreaObjectManager#{container.AreaObjectManagerIndex} / objects#{container.ObjectsIndex} ({container.Objects.Count} objet(s))"));
                for (var i = 0; i < container.Objects.Count; i++)
                {
                    _gameDataObjectRows.Add(EditableGameDataAreaObjectRow.FromObject(container.Index, i, container.Objects[i].RawXml));
                }
            }

            GameDataObjectContainerComboBox.SelectedIndex = _gameDataObjectContainerLabels.Count > 0 ? 0 : -1;
            GameDataObjectsStatusTextBlock.Text = _gameDataObjectContainerLabels.Count == 0
                ? Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucun bloc GameObject/objects trouvé dans AreaManagerData.")
                : Anno1800IslandTool.Core.Localization.TextCatalog.F($"{_gameDataObjectRows.Count} objet(s) dans {_gameDataObjectContainerLabels.Count} container(s).");
        }
        catch (Exception ex)
        {
            GameDataObjectsStatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Erreur lecture GameData objects.");
            SelectionDetailsTextBox.Text = ex.ToString();
        }
    }

    private void ReloadGameDataObjectsButton_Click(object sender, RoutedEventArgs e)
        => RefreshGameDataObjectsEditor();

    private void AddGameDataObjectButton_Click(object sender, RoutedEventArgs e)
    {
        var containerIndex = ResolveSelectedGameDataObjectContainerIndex();
        if (containerIndex < 0)
        {
            MessageBox.Show(this, Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucun container <objects> disponible. Impossible d'ajouter un objet tant que le chemin AreaObjectManager/GameObject/objects n'existe pas dans gamedata.xml."), Anno1800IslandTool.Core.Localization.TextCatalog.T("GameData objects"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var nextIndex = _gameDataObjectRows.Count(r => r.ContainerIndex == containerIndex);
        var row = EditableGameDataAreaObjectRow.FromObject(containerIndex, nextIndex, BuildDefaultAreaObjectNoneXml());
        _gameDataObjectRows.Add(row);
        GameDataObjectsDataGrid.SelectedItem = row;
        GameDataObjectsDataGrid.ScrollIntoView(row);
        GameDataObjectsStatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Objet ajouté en mémoire. Clique sur Appliquer GameData pour modifier gamedata.xml.");
    }

    private void DuplicateGameDataObjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (GameDataObjectsDataGrid.SelectedItem is not EditableGameDataAreaObjectRow selected)
        {
            MessageBox.Show(this, Anno1800IslandTool.Core.Localization.TextCatalog.T("Sélectionne d'abord un objet GameData à dupliquer."), Anno1800IslandTool.Core.Localization.TextCatalog.T("GameData objects"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var clone = EditableGameDataAreaObjectRow.FromObject(selected.ContainerIndex, _gameDataObjectRows.Count(r => r.ContainerIndex == selected.ContainerIndex), selected.RawXml);
        _gameDataObjectRows.Add(clone);
        GameDataObjectsDataGrid.SelectedItem = clone;
        GameDataObjectsDataGrid.ScrollIntoView(clone);
        GameDataObjectsStatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Objet dupliqué en mémoire. Pense à modifier guid/ID si nécessaire, puis Appliquer GameData.");
    }

    private void RemoveGameDataObjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (GameDataObjectsDataGrid.SelectedItem is EditableGameDataAreaObjectRow row)
        {
            _gameDataObjectRows.Remove(row);
            RenumberGameDataObjectRows();
            GameDataObjectsStatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.T("Objet supprimé en mémoire. Clique sur Appliquer GameData pour modifier gamedata.xml.");
        }
    }

    private void ApplyGameDataObjectsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || string.IsNullOrWhiteSpace(_currentProject.RawContent.GameDataText))
        {
            MessageBox.Show(this, Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucun gamedata.xml chargé."), Anno1800IslandTool.Core.Localization.TextCatalog.T("GameData objects"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var snapshot = GameDataAreaObjectsXmlEditor.Extract(_currentProject.RawContent.GameDataText!);
            if (snapshot.Containers.Count == 0)
            {
                MessageBox.Show(this, Anno1800IslandTool.Core.Localization.TextCatalog.T("Aucun container AreaManagerData/Data/AreaObjectManager/GameObject/objects n'a été trouvé. La v39.2 n'ajoute pas encore un AreaObjectManager complet quand il est absent : elle édite les containers existants."), Anno1800IslandTool.Core.Localization.TextCatalog.T("GameData objects"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var rows = _gameDataObjectRows.Select(r => r.ToObjectEdit()).ToList();
            var updated = GameDataAreaObjectsXmlEditor.ReplaceObjects(_currentProject.RawContent.GameDataText!, snapshot, rows);
            var result = BusyWork.Run(this, () => _editor.ApplyRawText(_currentProject, "GameData", updated));
            if (!result.Succeeded)
            {
                MessageBox.Show(this, result.Message, Anno1800IslandTool.Core.Localization.TextCatalog.T("GameData objects"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            RefreshProjectDisplay();
            GameDataObjectsStatusTextBlock.Text = Anno1800IslandTool.Core.Localization.TextCatalog.F($"GameData appliqué : {rows.Count} bloc(s) <None> dans <objects>.");
            MessageBox.Show(this, Anno1800IslandTool.Core.Localization.TextCatalog.T("Les <objects> AreaObjectManager ont été appliqués dans gamedata.xml."), Anno1800IslandTool.Core.Localization.TextCatalog.T("GameData objects"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), Anno1800IslandTool.Core.Localization.TextCatalog.T("GameData objects"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void GameDataObjectsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameDataObjectsDataGrid.SelectedItem is not EditableGameDataAreaObjectRow row)
        {
            return;
        }

        SelectionDetailsTextBox.Text =
            $"GameData AreaObjectManager object\n" +
            $"Container : {row.ContainerIndex}\n" +
            $"Index : {row.ObjectIndexText}\n" +
            $"guid : {row.Guid}\n" +
            $"ID : {row.ObjectId}\n" +
            $"ObjectFolderID : {row.FolderId}\n" +
            $"Position : {row.Position}\n\n" +
            row.RawXml;
    }

    private int ResolveSelectedGameDataObjectContainerIndex()
    {
        if (GameDataObjectsDataGrid.SelectedItem is EditableGameDataAreaObjectRow row)
        {
            return row.ContainerIndex;
        }

        var label = GameDataObjectContainerComboBox.SelectedItem?.ToString() ?? string.Empty;
        var match = Regex.Match(label, @"^\s*(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return _gameDataObjectSnapshot?.Containers.FirstOrDefault()?.Index ?? -1;
    }

    private void RenumberGameDataObjectRows()
    {
        foreach (var group in _gameDataObjectRows.GroupBy(r => r.ContainerIndex))
        {
            var index = 0;
            foreach (var row in group)
            {
                row.ObjectIndexText = index.ToString(CultureInfo.InvariantCulture);
                index++;
            }
        }
    }

    private static string BuildDefaultAreaObjectNoneXml()
        => "<None>\n" +
           "  <guid>0</guid>\n" +
           "  <ID>0</ID>\n" +
           "  <ObjectFolderID>0</ObjectFolderID>\n" +
           "  <Position>0 0 0</Position>\n" +
           "  <Direction>0</Direction>\n" +
           "</None>";

    public sealed class EditableGameDataAreaObjectRow : INotifyPropertyChanged
    {
        private string _containerIndexText = "0";
        private string _objectIndexText = "0";
        private string _rawXml = "<None></None>";

        public string ContainerIndexText
        {
            get => _containerIndexText;
            set { _containerIndexText = value; OnPropertyChanged(nameof(ContainerIndexText)); OnPropertyChanged(nameof(ContainerIndex)); }
        }

        public int ContainerIndex
            => int.TryParse(ContainerIndexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1;

        public string ObjectIndexText { get => _objectIndexText; set { _objectIndexText = value; OnPropertyChanged(nameof(ObjectIndexText)); } }

        public string RawXml
        {
            get => _rawXml;
            set
            {
                _rawXml = value ?? string.Empty;
                OnPropertyChanged(nameof(RawXml));
                OnPropertyChanged(nameof(Guid));
                OnPropertyChanged(nameof(ObjectId));
                OnPropertyChanged(nameof(FolderId));
                OnPropertyChanged(nameof(Position));
                OnPropertyChanged(nameof(Summary));
            }
        }

        public string Guid => ReadSimpleTag(RawXml, "guid");
        public string ObjectId => ReadSimpleTag(RawXml, "ID");
        public string FolderId => ReadSimpleTag(RawXml, "ObjectFolderID");
        public string Position => ReadSimpleTag(RawXml, "Position");
        public string Summary
        {
            get
            {
                var compact = Regex.Replace(RawXml ?? string.Empty, @"\s+", " ").Trim();
                if (compact.Length > 110)
                {
                    compact = compact.Substring(0, 110) + "…";
                }
                return compact;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public AreaObjectEdit ToObjectEdit() => new(ContainerIndex, GameDataAreaObjectsXmlEditor.EnsureNoneBlock(RawXml));

        public static EditableGameDataAreaObjectRow FromObject(int containerIndex, int objectIndex, string rawXml) => new()
        {
            ContainerIndexText = containerIndex.ToString(CultureInfo.InvariantCulture),
            ObjectIndexText = objectIndex.ToString(CultureInfo.InvariantCulture),
            RawXml = rawXml
        };

        private static string ReadSimpleTag(string text, string tag)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var match = Regex.Match(text, $@"<{Regex.Escape(tag)}>\s*(?<v>[^<\r\n]*)", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups["v"].Value.Trim() : string.Empty;
        }

        private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

