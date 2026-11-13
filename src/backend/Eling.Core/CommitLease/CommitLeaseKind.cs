namespace Eling.Core.CommitLease;

/// <summary>
/// Discriminator for the kind of resource a lease protects. The store keeps a
/// single record shape for both kinds so one file serves every caller.
/// </summary>
public enum CommitLeaseKind
{
    File = 0,
    Commit = 1,
}
