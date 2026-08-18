# AzToMarkdown metadata reference

This reference describes the Azure data consumed by the current tenant-to-vault pipeline and
the corresponding vault representation.

## Azure transport surface

All Azure access in `AzToMarkdown.Core` goes through `IArgQueryClient`.

| Method | Live implementation | Vault-backed implementation |
|---|---|---|
| `RunQueryAsync(kql)` | Paged `az graph query` | Ordered KQL pattern handlers over `VaultIndex` |
| `GetResourceByIdAsync(id)` | `az resource show --ids` | Exact vault-node lookup |
| `GetResourceByIdAsync(id, useRestPath: true)` | `az rest GET` | Node or child-collection lookup |
| `BatchArmGetAsync(urls)` | ARM `/batch` | Repeated vault lookups |
| `RunAksCommandAsync(rg, cluster, command)` | `az aks command invoke` | Non-zero result because a live cluster is required |
| `ListAcrRepositoriesAsync(registry, subscription)` | `az acr repository list` | Repository nodes below the registry |
| `FetchSubscriptionNamesAsync()` | `az account list` | `_summary.md` subscriptions map |

The CLI pipeline uses `RunQueryAsync`, `ListAcrRepositoriesAsync`, and `FetchSubscriptionNamesAsync`.
The other methods are available to Core consumers and are covered by unit or integration tests.

## Tenant enumeration

`TenantEnumerator.FetchAllAsync()` issues these three ARG queries in parallel:

| ARG table | Purpose | Projected data |
|---|---|---|
| `Resources` | Tenant resources | id, name, type, subscription, resource group, location, complete properties, tags, kind, sku, identity |
| `ResourceContainers` | Resource-group nodes | the same projection, with the group name used as its resource group |
| `AuthorizationResources` | Role assignments | id, name, type, subscription, resource group, location, complete properties |

It also calls `az account list` for subscription display names and `az acr repository list` once per
registry because ACR repositories are not available in ARG.

## Relationship property paths

Every path below is inside the ARG properties bag unless noted.

| Resource type | Property paths consumed |
|---|---|
| microsoft.network/publicipaddresses | ipConfiguration.id |
| microsoft.network/loadbalancers | frontendIPConfigurations[].properties.publicIPAddress.id; backendAddressPools[].properties.backendIPConfigurations[].id |
| microsoft.network/networkinterfaces | virtualMachine.id; ipConfigurations[].properties.subnet.id; ipConfigurations[].properties.publicIPAddress.id |
| microsoft.network/applicationgateways | frontendIPConfigurations[].properties.publicIPAddress.id; firewallPolicy.id; backendAddressPools[].properties.backendAddresses[].ipAddress or fqdn |
| microsoft.network/frontdoors | backendPools[].properties.backends[].address |
| microsoft.cdn/profiles | Child-resource IDs under the profile |
| microsoft.web/sites | serverFarmId; virtualNetworkSubnetId; siteConfig.linuxFxVersion or windowsFxVersion |
| microsoft.containerservice/managedclusters | agentPoolProfiles[].subnetId |
| microsoft.compute/virtualmachines | networkProfile.networkInterfaces[].id |
| microsoft.compute/virtualmachinescalesets | virtualMachineProfile.networkProfile.networkInterfaceConfigurations[].properties.ipConfigurations[].properties.subnet.id |
| DNS and private-DNS record types | ARecords[].ipv4Address; CNAMERecord.cname |
| microsoft.network/privateendpoints | subnet.id; privateLinkServiceConnections[].properties.privateLinkServiceId |
| microsoft.app/containerapps | managedEnvironmentId; template.containers[].image |
| microsoft.authorization/roleassignments | roleDefinitionId; scope; principalId |
| Resource types with at least two / separators | Parent relationship derived from the resource ID |

`azure_metadata.properties` stores the complete property bag, so every consumed path is preserved
without a per-type schema. `azure_metadata` also preserves tags and the top-level `kind`, `sku`, and
`identity` columns. Resource identity is stored under `resource`, graph edges under `relationships`,
role assignments under `role_assignments`, and subscription names in `_summary.md`.
