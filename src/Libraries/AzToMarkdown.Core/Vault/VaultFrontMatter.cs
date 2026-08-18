using System.Text.Json;
using AzToMarkdown.Core.Models;

namespace AzToMarkdown.Core.Vault;

/// <summary>
/// A normalized cross-resource reference persisted in the <c>relationships:</c> front-matter list.
/// <paramref name="Name"/>/<paramref name="Type"/> are null when the target is unknown to the graph.
/// </summary>
public sealed record VaultRelationship(
    string  Id,
    string? Name,
    string? Type,
    string  Direction,   // "inbound" | "outbound"
    string  Label);

/// <summary>
/// A role assignment embedded losslessly in the scoped resource's front-matter
/// (or in the vault-root <c>_role_assignments.md</c> for orphan scopes).
/// </summary>
public sealed record VaultRoleAssignment(
    string      Id,
    string      Role,
    string      PrincipalId,
    JsonElement Properties);

/// <summary>Everything <see cref="FrontMatterSerializer"/> needs to emit one resource's front-matter.</summary>
public sealed record FrontMatterContext(
    TenantNode                                          Node,
    string                                              SubscriptionName,
    IReadOnlyList<VaultRelationship>                    Relationships,
    IReadOnlyList<VaultRoleAssignment>                  RoleAssignments,
    string?                                             Version,
    IReadOnlyList<KeyValuePair<string, string>>         ExtraFlatKeys);

/// <summary>Result of parsing one vault .md file's front-matter (see VaultReader).</summary>
public sealed record ParsedVaultFile(
    int                                 SchemaVersion,
    string                              AzToMarkdownVersion,
    TenantNode                          Node,
    string                              SubscriptionName,
    IReadOnlyList<VaultRelationship>    Relationships,
    IReadOnlyList<VaultRoleAssignment>  RoleAssignments,
    string                              FilePath);
