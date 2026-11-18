using Bwc.Gpo.Pol;

namespace Bwc.Gpo.Tests;

/// <summary>
/// The one place the store tests call <c>PolDocument.SetValue</c>, the in-memory Registry.pol model. No registry API
/// is reached (scripts/check-registry-writer.js allowlists calls on unnamed receivers file by file).
/// </summary>
internal static class GpoStorePolHelper
{
    public static void SetDword(PolDocument document, string key, string name, uint value) =>
        document.SetValue(key, name, PolValueType.DWord, BitConverter.GetBytes(value));
}
