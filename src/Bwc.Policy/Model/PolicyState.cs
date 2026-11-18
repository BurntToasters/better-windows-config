namespace Bwc.Policy.Model;

/// <summary>The state of a policy as classic shows it.</summary>
public enum PolicyState
{
    /// <summary>The policy writes nothing and the file says nothing about it.</summary>
    NotConfigured,

    /// <summary>The policy is turned on.</summary>
    Enabled,

    /// <summary>The policy is turned off.</summary>
    Disabled,
}
