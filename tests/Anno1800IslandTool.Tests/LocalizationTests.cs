using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anno1800IslandTool.Core.Localization;
using Anno1800IslandTool.Formats.Readers;
using Anno1800IslandTool.Formats.Writers;
using Xunit;

namespace Anno1800IslandTool.Tests;

[CollectionDefinition("Localization", DisableParallelization = true)]
public class LocalizationCollection { }

[Collection("Localization")]
public class LocalizationTests
{
    [Fact]
    public void LanguageDoesNotChangeBiomeRecipesOrIntegrationXml()
    {
        var library = new Anno1800IslandTool.Pipeline.Services.EmbeddedMaterialSetLibrary();
        try
        {
            foreach (var name in new[] { "terra_01.xml", "south_america_01.xml", "colony02_01.xml" })
            {
                Assert.True(library.TryReadBundled(name, out var text));
                var set = new MaterialSetReader().Read(name, text);
                string? expected = null;
                string? expectedXml = null;
                foreach (var language in new[] { "fr", "en", "de" })
                {
                    TextCatalog.Language = language;
                    var profile = Anno1800IslandTool.Core.Terrain.TerrainTopologyProfile.Create(set);
                    var options = JsonSerializer.Serialize(profile.Options);
                    expected ??= options;
                    Assert.Equal(expected, options);
                    var project = new Anno1800IslandTool.Core.Models.IslandProject();
                    project.Metadata.IslandInternalName = "test_island";
                    var templates = Anno1800IslandTool.Pipeline.Export.IslandIntegrationTemplates.Create(project);
                    var xml = string.Join("\n", templates.Where(kv => kv.Key.EndsWith(".xml")).Select(kv => kv.Value));
                    expectedXml ??= xml;
                    Assert.Equal(expectedXml, xml);
                }
            }
        }
        finally { TextCatalog.Language = "fr"; }
    }

    [Fact]
    public void EveryCatalogueFormatRendersInAllThreeLanguages()
    {
        using var stream = typeof(TextCatalog).Assembly.GetManifestResourceStream("Anno1800IslandTool.Core.Localization.Catalog.json")!;
        var entries = JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream)!;
        try
        {
            foreach (var (source, translations) in entries)
            {
                Assert.All(translations, t => Assert.False(string.IsNullOrWhiteSpace(t)));
                var indices = Regex.Matches(source, @"(?<!\{)\{(\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToArray();
                if (indices.Length == 0) continue;
                var values = Enumerable.Range(0, indices.Max() + 1).Select(i => (object)("ARG_" + i)).ToArray();
                foreach (var language in new[] { "fr", "en", "de" })
                {
                    TextCatalog.Language = language;
                    var output = TextCatalog.F(FormattableStringFactory.Create(source, values));
                    foreach (var i in indices) Assert.Contains("ARG_" + i, output);
                }
            }
        }
        finally { TextCatalog.Language = "fr"; }
    }

    [Fact]
    public void DynamicMessagesRetainFormatsAndCanSwitchLanguage()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            TextCatalog.Language = "en";
            var message = TextCatalog.F($"Hauteur cible : {12.345,10:0.00}");
            Assert.Equal("Target height:      12.35", message);
            TextCatalog.Language = "de";
            Assert.Equal("Zielhöhe:      12.35", TextCatalog.Display(message));
            TextCatalog.Language = "fr";
            Assert.Equal("Hauteur cible :      12.35", TextCatalog.Display(message));
            TextCatalog.Language = "de";
            Assert.Equal("<StreetMap>Type FileName 1201 1048</StreetMap>", TextCatalog.Display("<StreetMap>Type FileName 1201 1048</StreetMap>"));
            Assert.Equal("H:/île/Annuler.xml", TextCatalog.T("H:/île/Annuler.xml"));
        }
        finally { TextCatalog.Language = "fr"; CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void MaterialXmlAndFieldNamesAreIndependentOfLanguage()
    {
        const string input = "<MaterialSet><Material><ID>10</><Type>0</><FileName>data/terrain.xml</><Repetition>1.5</><GroundType>Land</><ObjectFileName>data/tree.prp</></></>";
        string? expected = null;
        try
        {
            foreach (var language in new[] { "fr", "en", "de" })
            {
                TextCatalog.Language = language;
                var material = new MaterialSetReader().Read("terra_01.xml", input);
                Assert.Equal("0", material.Materials.Single().Type);
                Assert.Equal("data/terrain.xml", material.Materials.Single().FileName);
                var output = new MaterialSetWriter().Write(material);
                expected ??= output;
                Assert.Equal(expected, output);
                foreach (var name in new[] { "Type", "FileName", "FileNames", "StreetMap", "GlobalAmbientName", "material_sets", "MapSize" }) Assert.Equal(name, TextCatalog.T(name));
            }
        }
        finally { TextCatalog.Language = "fr"; }
    }
}
