using Bwc.Gpo.Store;
using Bwc.Policy.Admx;

namespace Bwc.Gpedit.Editor.Dialog;

/// <summary>A policy and the side whose file it is edited in (a class Both policy is edited once per side).</summary>
/// <param name="Policy">The policy definition.</param>
/// <param name="Side">The side.</param>
public sealed record PolicyRef(PolicyDefinition Policy, GpoSide Side);
