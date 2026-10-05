using Xunit;
using Anno1800IslandTool.Pipeline.Services;
namespace Anno1800IslandTool.Tests;
public class RdaResourceTests
{
 [Fact] public void WorkspaceIsBesideTools() => Assert.Equal(Path.Combine(AppContext.BaseDirectory,"workspace"), RdaGameResources.WorkspaceFolder);
 [Fact] public void PsdAndBrpReferencesResolveNativeCandidates()
 {
  Assert.Contains("data/graphics/test_0.dds", RdaGameResources.ReferenceCandidates("data/graphics/test.psd"));
  Assert.Contains("data/graphics/test.prp", RdaGameResources.ReferenceCandidates("data/graphics/test.brp"));
  Assert.Contains("data/graphics/test_lod2.rdm", RdaGameResources.ReferenceCandidates("data/graphics/test_lod0.rdm"));
 }
 [Theory][InlineData("../private.dds")][InlineData("data/../../private.dds")][InlineData("C:/private.dds")]
 public void RejectEscapingReferences(string path) => Assert.Empty(RdaGameResources.ReferenceCandidates(path));

 [Fact] public void SharedPrefixesFitOnePassAndMatchOnlyRequestedFiles()
 {
  var names = Enumerable.Range(0, 900).Select(i => $"data/graphics/props/vegetation/group_{i/30}/tree_{i}_diff_0.dds").ToArray();
  var batch = Assert.Single(RdaGameResources.BuildBatches(names));
  var pattern = new System.Text.RegularExpressions.Regex(batch.Filter);
  Assert.All(names, n => Assert.True(pattern.IsMatch(n)));
  Assert.False(pattern.IsMatch("data/graphics/props/vegetation/group_0/tree_999_diff_0.dds"));
  Assert.False(pattern.IsMatch(names[0]+".bak"));
 }
 [Fact] public void EscapedFilenamesAndTerminalPrefixesAreExact()
 {
  string[] names = ["data/a+[b].dds", "data/a+[b].dds_0.dds"];
  var pattern = new System.Text.RegularExpressions.Regex(RdaGameResources.BuildResourceFilter(names));
  Assert.All(names, n => Assert.True(pattern.IsMatch(n)));
  Assert.False(pattern.IsMatch("data/ab.dds"));
  Assert.Empty(RdaGameResources.BuildBatches([]));
 }
}
