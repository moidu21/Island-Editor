using System.Text.RegularExpressions;
using Anno1800IslandTool.Pipeline.Services;
using Xunit;

namespace Anno1800IslandTool.Tests;

public class DlcResourceFilterTests
{
    [Theory]
    [InlineData("data/dlc06/graphics/landscape/terrain/colony02/colony02_beach_06.xml", true)]
    [InlineData("data/dlc16/graphics/props/test.prp", true)]
    [InlineData("data/dlc10/graphics/buildings/test.cfg", true)]
    [InlineData("data/graphics/landscape/terrain/terra/m_gras_02.xml", true)]
    [InlineData("data/config/engine/material_sets/colony02_01.xml", true)]
    [InlineData("data/dlc03/config/engine/material_sets/arctic_01.xml", true)]
    [InlineData("data/config/export/main/asset/assets.xml", true)]
    [InlineData("data/dlc06/graphics/landscape/terrain/colony02/beach_diff_0.dds", false)]
    [InlineData("data/dlc16/graphics/props/test_lod0.rdm", false)]
    public void DefinitionsIncludeDlcButLargeResourcesStayOnDemand(string path, bool expected)
        => Assert.Equal(expected, Regex.IsMatch(path, RdaGameResources.MetadataFilter));
}
