namespace Bwc.Gpo.Pol;

/// <summary>The registry value types the edit API can create. A loaded record may carry any other number.</summary>
public enum PolValueType : uint
{
    /// <summary>REG_SZ.</summary>
    String = 1,

    /// <summary>REG_EXPAND_SZ.</summary>
    ExpandString = 2,

    /// <summary>REG_BINARY.</summary>
    Binary = 3,

    /// <summary>REG_DWORD.</summary>
    DWord = 4,

    /// <summary>REG_MULTI_SZ.</summary>
    MultiString = 7,

    /// <summary>REG_QWORD.</summary>
    QWord = 11,
}
