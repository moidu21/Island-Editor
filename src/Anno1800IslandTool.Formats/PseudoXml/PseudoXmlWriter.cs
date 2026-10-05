namespace Anno1800IslandTool.Formats.PseudoXml;

public sealed class PseudoXmlWriter
{
    public string Write(PseudoXmlDocument document) => document.RawText;
}
