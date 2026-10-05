namespace Anno1800IslandTool.Core.Validation;

public static class ValidationCode
{
    public const string MissingRootFile = "VAL001";
    public const string MissingRd3dFile = "VAL002";
    public const string MissingGameDataFile = "VAL003";
    public const string MissingRawRootText = "VAL004";
    public const string MissingRawRd3dText = "VAL005";
    public const string MissingRawGameDataText = "VAL006";
    public const string XmlDeclarationNotSupported = "VAL007";
    public const string ValidatorInternalError = "VAL008";

    public const string MissingMeshPages = "VAL010";
    public const string MissingNormalMapPages = "VAL011";
    public const string MissingTintMaps = "VAL012";
    public const string MissingTerrainCounterpart = "VAL013";

    public const string InconsistentDimensions = "VAL100";
    public const string InconsistentRootMetadata = "VAL101";
    public const string InvalidPageKey = "VAL102";
    public const string UnexpectedPlayableArea = "VAL103";
    public const string SuspiciousRd3dSetting = "VAL104";

    public const string MissingMaterialSet = "VAL200";
    public const string InvalidMaterialSet = "VAL201";
    public const string DuplicateMaterialId = "VAL202";
    public const string MissingPropReference = "VAL203";
    public const string DuplicatePropReference = "VAL204";

    public const string TerrainPageMismatch = "VAL300";
    public const string TerrainDiagnostic = "VAL301";
    public const string TerrainCoverageDiagnostic = "VAL302";
    public const string TerrainHeightEncoding = "VAL303";
    public const string MaterialMapEncoding = "VAL304";
    public const string GroundObjectDiagnostic = "VAL305";
    public const string GroundObjectInvalid = "VAL306";
    public const string MissingGameplayManager = "VAL400";
    public const string SparseGameplayData = "VAL401";
    public const string SuspiciousGameplayBlock = "VAL402";
}
