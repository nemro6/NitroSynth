namespace NitroSynth.App;

public enum HexSearchMethod
{
    BigEndian,
    LittleEndian
}

public enum HexSearchDataKind
{
    HexData,
    Text
}

public enum HexSearchRangeKind
{
    FromCursor,
    WholeData,
    Selection
}

public readonly record struct HexSearchRequest(
    string Query,
    HexSearchMethod Method,
    HexSearchDataKind DataKind,
    HexSearchRangeKind RangeKind);

