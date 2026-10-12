using System.Text.RegularExpressions;
namespace Anno1800IslandTool.UI;
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
            throw new ArgumentException(Anno1800IslandTool.Core.Localization.TextCatalog.T("Le nom du groupe d'objets est requis."), nameof(objectGroupName));
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
                        var absoluteContentStart = gameObjectBodyBase + objects.OpenStart;
                        var absoluteContentEnd = gameObjectBodyBase + objects.CloseEnd;
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
            var newBody = "<objects>" + BuildObjectsBody(container, objects) + "</objects>";
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
