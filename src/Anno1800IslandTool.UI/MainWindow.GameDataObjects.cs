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
        GameDataObjectsStatusTextBlock.Text = "Aucun projet chargé.";
    }

    private void RefreshGameDataObjectsEditor()
    {
        _gameDataObjectRows.Clear();
        _gameDataObjectContainerLabels.Clear();
        _gameDataObjectSnapshot = null;

        if (_currentProject is null || string.IsNullOrWhiteSpace(_currentProject.RawContent.GameDataText))
        {
            GameDataObjectsStatusTextBlock.Text = "Aucun gamedata.xml chargé.";
            return;
        }

        try
        {
            _gameDataObjectSnapshot = GameDataAreaObjectsXmlEditor.Extract(_currentProject.RawContent.GameDataText!);
            foreach (var container in _gameDataObjectSnapshot.Containers)
            {
                _gameDataObjectContainerLabels.Add($"{container.Index} - AreaManagerData#{container.AreaManagerDataIndex} / AreaObjectManager#{container.AreaObjectManagerIndex} / objects#{container.ObjectsIndex} ({container.Objects.Count} objet(s))");
                for (var i = 0; i < container.Objects.Count; i++)
                {
                    _gameDataObjectRows.Add(EditableGameDataAreaObjectRow.FromObject(container.Index, i, container.Objects[i].RawXml));
                }
            }

            GameDataObjectContainerComboBox.SelectedIndex = _gameDataObjectContainerLabels.Count > 0 ? 0 : -1;
            GameDataObjectsStatusTextBlock.Text = _gameDataObjectContainerLabels.Count == 0
                ? "Aucun bloc GameObject/objects trouvé dans AreaManagerData."
                : $"{_gameDataObjectRows.Count} objet(s) dans {_gameDataObjectContainerLabels.Count} container(s).";
        }
        catch (Exception ex)
        {
            GameDataObjectsStatusTextBlock.Text = "Erreur lecture GameData objects.";
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
            MessageBox.Show(this, "Aucun container <objects> disponible. Impossible d'ajouter un objet tant que le chemin AreaObjectManager/GameObject/objects n'existe pas dans gamedata.xml.", "GameData objects", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var nextIndex = _gameDataObjectRows.Count(r => r.ContainerIndex == containerIndex);
        var row = EditableGameDataAreaObjectRow.FromObject(containerIndex, nextIndex, BuildDefaultAreaObjectNoneXml());
        _gameDataObjectRows.Add(row);
        GameDataObjectsDataGrid.SelectedItem = row;
        GameDataObjectsDataGrid.ScrollIntoView(row);
        GameDataObjectsStatusTextBlock.Text = "Objet ajouté en mémoire. Clique sur Appliquer GameData pour modifier gamedata.xml.";
    }

    private void DuplicateGameDataObjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (GameDataObjectsDataGrid.SelectedItem is not EditableGameDataAreaObjectRow selected)
        {
            MessageBox.Show(this, "Sélectionne d'abord un objet GameData à dupliquer.", "GameData objects", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var clone = EditableGameDataAreaObjectRow.FromObject(selected.ContainerIndex, _gameDataObjectRows.Count(r => r.ContainerIndex == selected.ContainerIndex), selected.RawXml);
        _gameDataObjectRows.Add(clone);
        GameDataObjectsDataGrid.SelectedItem = clone;
        GameDataObjectsDataGrid.ScrollIntoView(clone);
        GameDataObjectsStatusTextBlock.Text = "Objet dupliqué en mémoire. Pense à modifier guid/ID si nécessaire, puis Appliquer GameData.";
    }

    private void RemoveGameDataObjectButton_Click(object sender, RoutedEventArgs e)
    {
        if (GameDataObjectsDataGrid.SelectedItem is EditableGameDataAreaObjectRow row)
        {
            _gameDataObjectRows.Remove(row);
            RenumberGameDataObjectRows();
            GameDataObjectsStatusTextBlock.Text = "Objet supprimé en mémoire. Clique sur Appliquer GameData pour modifier gamedata.xml.";
        }
    }

    private void ApplyGameDataObjectsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null || string.IsNullOrWhiteSpace(_currentProject.RawContent.GameDataText))
        {
            MessageBox.Show(this, "Aucun gamedata.xml chargé.", "GameData objects", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var snapshot = GameDataAreaObjectsXmlEditor.Extract(_currentProject.RawContent.GameDataText!);
            if (snapshot.Containers.Count == 0)
            {
                MessageBox.Show(this, "Aucun container AreaManagerData/Data/AreaObjectManager/GameObject/objects n'a été trouvé. La v39.2 n'ajoute pas encore un AreaObjectManager complet quand il est absent : elle édite les containers existants.", "GameData objects", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var rows = _gameDataObjectRows.Select(r => r.ToObjectEdit()).ToList();
            var updated = GameDataAreaObjectsXmlEditor.ReplaceObjects(_currentProject.RawContent.GameDataText!, snapshot, rows);
            var result = _editor.ApplyRawText(_currentProject, "GameData", updated);
            if (!result.Succeeded)
            {
                MessageBox.Show(this, result.Message, "GameData objects", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            RefreshProjectDisplay();
            GameDataObjectsStatusTextBlock.Text = $"GameData appliqué : {rows.Count} bloc(s) <None> dans <objects>.";
            MessageBox.Show(this, "Les <objects> AreaObjectManager ont été appliqués dans gamedata.xml.", "GameData objects", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "GameData objects", MessageBoxButton.OK, MessageBoxImage.Error);
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

public sealed record AreaObjectEdit(int ContainerIndex, string RawXml);
internal sealed record AreaObjectBlock(string RawXml);

internal sealed class GameDataAreaObjectsSnapshot
{
    public List<GameDataAreaObjectsContainer> Containers { get; } = new();
}

internal sealed class GameDataAreaObjectsContainer
{
    public int Index { get; init; }
    public int AreaManagerDataIndex { get; init; }
    public int AreaObjectManagerIndex { get; init; }
    public int ObjectsIndex { get; init; }
    public int ContentStart { get; init; }
    public int ContentEnd { get; init; }
    public string OriginalBody { get; init; } = string.Empty;
    public string ItemIndent { get; init; } = "                  ";
    public string ClosingIndent { get; init; } = "                ";
    public List<AreaObjectBlock> Objects { get; } = new();
}

internal static class GameDataAreaObjectsXmlEditor
{
    public static GameDataAreaObjectsSnapshot Extract(string text, string objectGroupName = "GameObject")
    {
        if (string.IsNullOrWhiteSpace(objectGroupName))
        {
            throw new ArgumentException("Le nom du groupe d'objets est requis.", nameof(objectGroupName));
        }

        var snapshot = new GameDataAreaObjectsSnapshot();
        var containerIndex = 0;
        var areaManagerDataIndex = 0;

        foreach (var areaData in FindBlocks(text, "AreaManagerData"))
        {
            var areaBodyBase = areaData.ContentStart;
            var areaObjectManagerIndex = 0;
            foreach (var manager in FindBlocks(areaData.Body, "AreaObjectManager"))
            {
                var managerBodyBase = areaBodyBase + manager.ContentStart;
                foreach (var gameObject in FindBlocks(manager.Body, objectGroupName))
                {
                    var gameObjectBodyBase = managerBodyBase + gameObject.ContentStart;
                    var objectsIndex = 0;
                    foreach (var objects in FindBlocks(gameObject.Body, "objects"))
                    {
                        var absoluteContentStart = gameObjectBodyBase + objects.ContentStart;
                        var absoluteContentEnd = gameObjectBodyBase + objects.ContentEnd;
                        var container = new GameDataAreaObjectsContainer
                        {
                            Index = containerIndex++,
                            AreaManagerDataIndex = areaManagerDataIndex,
                            AreaObjectManagerIndex = areaObjectManagerIndex,
                            ObjectsIndex = objectsIndex,
                            ContentStart = absoluteContentStart,
                            ContentEnd = absoluteContentEnd,
                            OriginalBody = objects.Body,
                            ItemIndent = InferItemIndent(objects.Body),
                            ClosingIndent = InferClosingIndent(text, absoluteContentEnd)
                        };

                        foreach (var none in FindBlocks(objects.Body, "None"))
                        {
                            container.Objects.Add(new AreaObjectBlock(none.FullText.Trim()));
                        }

                        snapshot.Containers.Add(container);
                        objectsIndex++;
                    }
                }

                areaObjectManagerIndex++;
            }

            areaManagerDataIndex++;
        }

        return snapshot;
    }

    public static string ReplaceObjects(string text, GameDataAreaObjectsSnapshot snapshot, IReadOnlyList<AreaObjectEdit> edits)
    {
        var byContainer = edits
            .Where(e => e.ContainerIndex >= 0)
            .GroupBy(e => e.ContainerIndex)
            .ToDictionary(g => g.Key, g => g.Select(e => EnsureNoneBlock(e.RawXml)).ToList());

        var result = text;
        foreach (var container in snapshot.Containers.OrderByDescending(c => c.ContentStart))
        {
            byContainer.TryGetValue(container.Index, out var objects);
            objects ??= new List<string>();
            var newBody = BuildObjectsBody(container, objects);
            result = result.Substring(0, container.ContentStart) + newBody + result.Substring(container.ContentEnd);
        }

        return result;
    }

    public static string EnsureNoneBlock(string rawXml)
    {
        var trimmed = NormalizeLineEndings(rawXml ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "<None></None>";
        }

        if (Regex.IsMatch(trimmed, @"^<None(?:\s[^>]*)?>", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(trimmed, @"</None>\s*$", RegexOptions.IgnoreCase))
        {
            return trimmed;
        }

        return "<None>\n" + trimmed + "\n</None>";
    }

    private static string BuildObjectsBody(GameDataAreaObjectsContainer container, IReadOnlyList<string> objects)
    {
        if (objects.Count == 0)
        {
            return "\n" + container.ClosingIndent;
        }

        var lines = new List<string> { string.Empty };
        foreach (var raw in objects)
        {
            var normalized = Dedent(EnsureNoneBlock(raw));
            foreach (var line in normalized.Split('\n'))
            {
                lines.Add(container.ItemIndent + line.TrimEnd('\r'));
            }
        }

        lines.Add(container.ClosingIndent);
        return string.Join("\n", lines);
    }

    private static string InferItemIndent(string body)
    {
        var normalized = NormalizeLineEndings(body);
        var match = Regex.Match(normalized, @"\n(?<indent>\s*)<None(?:\s|>)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups["indent"].Value;
        }

        return "                  ";
    }

    private static string InferClosingIndent(string fullText, int contentEnd)
    {
        var start = Math.Max(0, contentEnd - 120);
        var prefix = NormalizeLineEndings(fullText.Substring(start, contentEnd - start));
        var lastNewLine = prefix.LastIndexOf('\n');
        if (lastNewLine >= 0)
        {
            var trailing = prefix[(lastNewLine + 1)..];
            if (trailing.All(char.IsWhiteSpace))
            {
                return trailing;
            }
        }

        return "                ";
    }

    private static string Dedent(string text)
    {
        var normalized = NormalizeLineEndings(text).Trim();
        var lines = normalized.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var nonBlank = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        if (nonBlank.Length == 0)
        {
            return string.Empty;
        }

        var minIndent = nonBlank
            .Select(l => l.TakeWhile(char.IsWhiteSpace).Count())
            .DefaultIfEmpty(0)
            .Min();
        if (minIndent <= 0)
        {
            return string.Join("\n", lines);
        }

        return string.Join("\n", lines.Select(l => l.Length >= minIndent ? l[minIndent..] : l.TrimStart()));
    }

    private static string NormalizeLineEndings(string text)
        => (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

    private static List<XmlBlockSpan> FindBlocks(string text, string tag)
    {
        var result = new List<XmlBlockSpan>();
        var cursor = 0;
        while (cursor < text.Length)
        {
            var block = FindNextBlock(text, tag, cursor);
            if (block is null)
            {
                break;
            }

            result.Add(block);
            cursor = Math.Max(block.CloseEnd, block.OpenStart + 1);
        }

        return result;
    }

    private static XmlBlockSpan? FindNextBlock(string text, string tag, int startIndex)
    {
        var tokenRegex = new Regex($@"<\s*(?<close>/?)\s*{Regex.Escape(tag)}\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase);
        var open = tokenRegex.Match(text, startIndex);
        while (open.Success && open.Groups["close"].Value == "/")
        {
            open = open.NextMatch();
        }

        if (!open.Success)
        {
            return null;
        }

        var openStart = open.Index;
        var openEnd = open.Index + open.Length;
        if (open.Groups["attrs"].Value.TrimEnd().EndsWith("/", StringComparison.Ordinal))
        {
            return new XmlBlockSpan(openStart, openEnd, openEnd, openEnd, openEnd, string.Empty, open.Value);
        }

        var depth = 1;
        var scan = openEnd;
        while (scan < text.Length)
        {
            var token = tokenRegex.Match(text, scan);
            if (!token.Success)
            {
                return null;
            }

            var isClose = token.Groups["close"].Value == "/";
            var isSelfClosing = token.Groups["attrs"].Value.TrimEnd().EndsWith("/", StringComparison.Ordinal);
            if (isClose)
            {
                depth--;
                if (depth == 0)
                {
                    var contentEnd = token.Index;
                    var closeEnd = token.Index + token.Length;
                    var body = text.Substring(openEnd, contentEnd - openEnd);
                    var full = text.Substring(openStart, closeEnd - openStart);
                    return new XmlBlockSpan(openStart, openEnd, openEnd, contentEnd, closeEnd, body, full);
                }
            }
            else if (!isSelfClosing)
            {
                depth++;
            }

            scan = token.Index + token.Length;
        }

        return null;
    }

    private sealed record XmlBlockSpan(int OpenStart, int OpenEnd, int ContentStart, int ContentEnd, int CloseEnd, string Body, string FullText);
}
