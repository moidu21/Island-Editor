using System.Text;
using System.Text.RegularExpressions;

namespace Anno1800IslandTool.Pipeline.Editing;

internal static class PseudoXmlSafety
{
    public static PseudoXmlValidationResult ValidateSingleRoot(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return PseudoXmlValidationResult.Invalid("Le pseudo-XML est vide.");
        }

        var input = text!;
        var index = 0;
        var depth = 0;
        string? rootName = null;
        var rootCount = 0;

        while (index < input.Length)
        {
            var lt = input.IndexOf('<', index);
            if (lt < 0)
            {
                if (HasNonWhitespace(input, index, input.Length))
                {
                    return PseudoXmlValidationResult.Invalid("Du texte a été trouvé hors du nœud racine.");
                }

                break;
            }

            if (depth == 0 && HasNonWhitespace(input, index, lt))
            {
                return PseudoXmlValidationResult.Invalid("Du texte a été trouvé hors du nœud racine.");
            }

            if (lt + 1 >= input.Length)
            {
                return PseudoXmlValidationResult.Invalid("Balise incomplète en fin de document.");
            }

            if (StartsWith(input, lt, "<!--"))
            {
                var endComment = input.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                if (endComment < 0)
                {
                    return PseudoXmlValidationResult.Invalid("Commentaire pseudo-XML non fermé.");
                }

                index = endComment + 3;
                continue;
            }

            if (StartsWith(input, lt, "<?"))
            {
                var endPi = input.IndexOf("?>", lt + 2, StringComparison.Ordinal);
                if (endPi < 0)
                {
                    return PseudoXmlValidationResult.Invalid("Instruction de traitement non fermée.");
                }

                index = endPi + 2;
                continue;
            }

            if (StartsWith(input, lt, "<![CDATA["))
            {
                var endCData = input.IndexOf("]]>", lt + 9, StringComparison.Ordinal);
                if (endCData < 0)
                {
                    return PseudoXmlValidationResult.Invalid("Bloc CDATA non fermé.");
                }

                index = endCData + 3;
                continue;
            }

            if (StartsWith(input, lt, "<!"))
            {
                var gt = input.IndexOf('>', lt + 2);
                if (gt < 0)
                {
                    return PseudoXmlValidationResult.Invalid("Déclaration pseudo-XML non fermée.");
                }

                index = gt + 1;
                continue;
            }

            var gtIndex = input.IndexOf('>', lt + 1);
            if (gtIndex < 0)
            {
                return PseudoXmlValidationResult.Invalid("Balise non fermée.");
            }

            var rawTag = input.Substring(lt + 1, gtIndex - lt - 1).Trim();
            if (rawTag.Length == 0)
            {
                return PseudoXmlValidationResult.Invalid("Balise vide.");
            }

            var isClosing = rawTag[0] == '/';
            var isAnonymousClosing = string.Equals(rawTag, "/", StringComparison.Ordinal);
            var isSelfClosing = rawTag[^1] == '/' && !isAnonymousClosing;
            var tagName = isAnonymousClosing ? "/" : ExtractTagName(rawTag);
            if (string.IsNullOrWhiteSpace(tagName))
            {
                return PseudoXmlValidationResult.Invalid("Nom de balise introuvable.");
            }

            if (isClosing)
            {
                // Les materialsets Anno interprétés utilisent souvent des fermetures anonymes </>.
                // Elles ferment simplement le dernier nœud ouvert, sans répéter le nom de balise.
                depth--;
                if (depth < 0)
                {
                    return PseudoXmlValidationResult.Invalid("Fermeture de balise inattendue.");
                }
            }
            else if (!isSelfClosing)
            {
                if (depth == 0)
                {
                    rootCount++;
                    if (rootCount > 1)
                    {
                        return PseudoXmlValidationResult.Invalid("Plusieurs nœuds racine ont été détectés.");
                    }

                    rootName = tagName;
                }

                depth++;
            }
            else if (depth == 0)
            {
                rootCount++;
                if (rootCount > 1)
                {
                    return PseudoXmlValidationResult.Invalid("Plusieurs nœuds racine ont été détectés.");
                }

                rootName = tagName;
            }

            index = gtIndex + 1;
        }

        if (depth != 0)
        {
            return PseudoXmlValidationResult.Invalid("Le document contient des balises non fermées ou déséquilibrées.");
        }

        if (rootCount != 1)
        {
            return PseudoXmlValidationResult.Invalid("Le document ne contient pas exactement un nœud racine.");
        }

        return PseudoXmlValidationResult.Valid(rootName ?? string.Empty);
    }

    public static string EnsureSingleRootOrFallback(string? candidate, string fallback)
    {
        var validation = ValidateSingleRoot(candidate);
        if (validation.IsValid)
        {
            return NormalizeLineEndings(candidate!);
        }

        return NormalizeLineEndings(fallback);
    }

    public static string InsertBeforeOuterRootClosingTagOrAppend(string text, string fragment)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fragment;
        }

        var rootTag = TryReadOuterRootTagName(text);
        if (!string.IsNullOrWhiteSpace(rootTag))
        {
            var closeTag = $"</{rootTag}>";
            var idx = text.LastIndexOf(closeTag, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var insertion = fragment.EndsWith(Environment.NewLine, StringComparison.Ordinal) ? fragment : fragment + Environment.NewLine;
                return text.Insert(idx, insertion);
            }
        }

        return text + Environment.NewLine + fragment;
    }

    public static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);

    public static string? TryReadOuterRootTagName(string text)
    {
        var match = Regex.Match(text, @"<\s*(?<name>[A-Za-z_][A-Za-z0-9_\-:.]*)\b[^>]*>", RegexOptions.Singleline);
        return match.Success ? match.Groups["name"].Value : null;
    }

    private static bool HasNonWhitespace(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsWith(string text, int index, string value)
        => index >= 0 && index + value.Length <= text.Length && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

    private static string ExtractTagName(string rawTag)
    {
        var span = rawTag.AsSpan().Trim();
        if (span.IsEmpty)
        {
            return string.Empty;
        }

        if (span[0] == '/')
        {
            span = span[1..].TrimStart();
        }

        if (!span.IsEmpty && span[^1] == '/')
        {
            span = span[..^1].TrimEnd();
        }

        var end = 0;
        while (end < span.Length)
        {
            var c = span[end];
            if (char.IsWhiteSpace(c) || c == '/' || c == '>')
            {
                break;
            }

            end++;
        }

        return end <= 0 ? string.Empty : new string(span[..end]);
    }
}

internal sealed record PseudoXmlValidationResult(bool IsValid, string RootName, string? Error)
{
    public static PseudoXmlValidationResult Valid(string rootName) => new(true, rootName, null);
    public static PseudoXmlValidationResult Invalid(string error) => new(false, string.Empty, error);
}
