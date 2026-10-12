namespace Anno1800IslandTool.Formats.PseudoXml;

public sealed class PseudoXmlDocument
{
    public string RawText { get; }

    public PseudoXmlDocument(string rawText)
    {
        RawText = rawText;
    }
}
