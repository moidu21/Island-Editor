using System.IO;
using Anno1800IslandTool.Core.Models;

namespace Anno1800IslandTool.Pipeline.Services;

/// <summary>
/// Résout le materialset terrain effectif utilisé par les îles Anno 1800.
///
/// Les rd3d.xml vanilla fournis ne contiennent généralement pas de tag
/// &lt;MaterialSetFileName&gt;. Dans ce cas, le jeu utilise implicitement terra_01.xml.
/// Le resolver conserve cette convention : l'absence de référence explicite n'est pas une
/// erreur de structure et ne doit pas provoquer l'écriture d'un tag vide à l'export.
/// </summary>
public sealed class DefaultMaterialSetResolver
{
    public const string VanillaFallbackMaterialSet = "terra_01.xml";

    public string ResolveEffectiveName(Rd3dDefinition rd3d)
    {
        if (string.IsNullOrWhiteSpace(rd3d.MaterialSetFileName))
        {
            return VanillaFallbackMaterialSet;
        }

        var trimmed = rd3d.MaterialSetFileName.Trim();
        return Path.GetFileName(trimmed.Replace('\\', '/'));
    }
}
