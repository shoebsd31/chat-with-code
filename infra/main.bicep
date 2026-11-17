// Chat with code on Azure Container Apps.
//
// Creates everything the backend needs in one resource group, with no passwords or keys in
// configuration: the apps sign in to PostgreSQL, Key Vault, Storage, the container registry,
// Azure DevOps and (optionally) Azure OpenAI / Azure AI Search with one managed identity.
// The only secrets (extension secret, webhook password) live in Key Vault.
//
// Deployed in two passes by .github/workflows/deploy-azure.yml: first without the apps
// (deployApps = false) so the registry exists for the image build, then with the apps.

targetScope = 'resourceGroup'

@description('Short prefix for resource names (lowercase letters and digits).')
@minLength(3)
@maxLength(12)
param namePrefix string

param location string = resourceGroup().location

@description('Azure DevOps organization name (the part after https://dev.azure.com/).')
param adoOrganization string

@description('Sign-in emails of Chat with code admins, comma-separated.')
param chatAdmins string = ''

@description('Browser origin of the published extension, e.g. https://yourpublisher.gallerycdn.vsassets.io')
param extensionOrigin string = ''

@description('Create the container apps. False on the first pass, before images exist.')
param deployApps bool = true

@description('Image tag to run (the workflow uses the commit SHA).')
param imageTag string = 'latest'

@description('Minutes between checks for new commits; 0 = once when the worker starts.')
param pollIntervalMinutes int = 5

@description('Username Azure DevOps service hooks use. The password is the "webhook-password" Key Vault secret.')
param webhookUsername string = 'azdo-hooks'

@description('Object id of the identity running the deployment, so it can create the Key Vault secrets.')
param deployerPrincipalId string = ''

@description('Optional starting values for the organization defaults (saved once; then managed in Azure DevOps).')
param azureOpenAIEndpoint string = ''
param azureOpenAIChatDeployment string = ''
param azureOpenAIEmbeddingDeployment string = ''

@description('Optional resource id of the Azure OpenAI resource; the app identity gets "Cognitive Services OpenAI User" on it.')
param azureOpenAIResourceId string = ''

@description('Optional resource id of an Azure AI Search service; the app identity gets index data and service contributor roles on it.')
param azureSearchResourceId string = ''

@description('PostgreSQL compute size. Burstable B1ms is enough to start.')
param postgresSkuName string = 'Standard_B1ms'
param postgresTier string = 'Burstable'
param postgresVersion string = '16'

// Resource names allow different characters (the registry and storage account only lowercase letters
// and digits, container apps no "--"), so keep just lowercase letters and digits from the prefix.
var prefix = toLower(replace(replace(replace(replace(namePrefix, '-', ''), '_', ''), '.', ''), ' ', ''))
var suffix = uniqueString(resourceGroup().id, namePrefix)
var names = {
  identity: '${prefix}-id'
  logs: '${prefix}-logs'
  environment: '${prefix}-env'
  registry: '${prefix}acr${suffix}'
  vault: take('${prefix}-kv-${suffix}', 24)
  storage: take('${prefix}st${suffix}', 24)
  postgres: '${prefix}-pg-${suffix}'
  api: '${prefix}-api'
  worker: '${prefix}-worker'
}
var databaseName = 'chatwithcode'

// Built-in Azure role definition ids. These are public and the same in every Azure tenant
// (https://learn.microsoft.com/azure/role-based-access-control/built-in-roles), not secrets.
var roles = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  keyVaultSecretsOfficer: 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
  keyVaultCryptoUser: '12338af0-0e69-4776-bea7-57ae8d297424'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
}

// ---------------- Identity ----------------

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: names.identity
  location: location
}

// ---------------- Monitoring and environment ----------------

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: names.logs
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: names.environment
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

// ---------------- Container registry ----------------

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: names.registry
  location: location
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
  }
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, roles.acrPull)
  scope: registry
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
  }
}

// ---------------- Key Vault (secrets + the key that protects the encryption key ring) ----------------

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: names.vault
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 30
    enablePurgeProtection: true
  }
}

resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: vault
  name: 'dataprotection'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: ['wrapKey', 'unwrapKey']
  }
}

resource vaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, identity.id, roles.keyVaultSecretsUser)
  scope: vault
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
  }
}

resource vaultCryptoUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, identity.id, roles.keyVaultCryptoUser)
  scope: vault
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultCryptoUser)
  }
}

// The deployment identity creates missing secrets (with random values) so the apps can start.
resource vaultSecretsOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId)) {
  name: guid(vault.id, deployerPrincipalId, roles.keyVaultSecretsOfficer)
  scope: vault
  properties: {
    principalId: deployerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsOfficer)
  }
}

// ---------------- Storage for the encryption key ring ----------------

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: names.storage
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource keyRingContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'dataprotection'
}

resource blobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, identity.id, roles.storageBlobDataContributor)
  scope: storage
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataContributor)
  }
}

// ---------------- PostgreSQL (Entra sign-in only, pgvector allowed) ----------------

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: names.postgres
  location: location
  sku: {
    name: postgresSkuName
    tier: postgresTier
  }
  properties: {
    version: postgresVersion
    storage: { storageSizeGB: 32 }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: { mode: 'Disabled' }
    authConfig: {
      activeDirectoryAuth: 'Enabled'
      passwordAuth: 'Disabled'
      tenantId: subscription().tenantId
    }
  }
}

// The app identity is the server's Entra admin: it creates the schema and the vector extension on start.
module postgresAdmin 'modules/postgres-admin.bicep' = {
  name: 'postgres-admin'
  params: {
    serverName: postgres.name
    principalId: identity.properties.principalId
    principalName: identity.name
  }
}

// Server changes run one at a time (the server rejects parallel updates).
resource allowVector 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
  parent: postgres
  name: 'azure.extensions'
  properties: {
    value: 'VECTOR'
    source: 'user-override'
  }
  dependsOn: [postgresAdmin]
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: databaseName
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
  dependsOn: [allowVector]
}

// Container Apps reach the server over Azure's network. Sign-in is Entra-only.
resource allowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: postgres
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
  dependsOn: [database]
}

// ---------------- Optional access to existing AI resources ----------------

// Azure resolves a module's scope even when its condition is false, so an empty id must still
// split into enough parts: fall back to this resource group. (The workflow rejects ids that aren't
// full resource ids before deploying.)
var placeholderId = '${resourceGroup().id}/providers/none/none/none'
var openAIIdParts = split(empty(azureOpenAIResourceId) ? placeholderId : azureOpenAIResourceId, '/')
var searchIdParts = split(empty(azureSearchResourceId) ? placeholderId : azureSearchResourceId, '/')

module openAIAccess 'modules/openai-access.bicep' = if (!empty(azureOpenAIResourceId)) {
  name: 'openai-access'
  scope: resourceGroup(openAIIdParts[2], openAIIdParts[4])
  params: {
    accountName: last(openAIIdParts)
    principalId: identity.properties.principalId
  }
}

module searchAccess 'modules/search-access.bicep' = if (!empty(azureSearchResourceId)) {
  name: 'search-access'
  scope: resourceGroup(searchIdParts[2], searchIdParts[4])
  params: {
    serviceName: last(searchIdParts)
    principalId: identity.properties.principalId
  }
}

// ---------------- Apps ----------------

var connectionString = 'Host=${postgres.properties.fullyQualifiedDomainName};Port=5432;Database=${databaseName};Username=${identity.name};Ssl Mode=Require;GSS Encryption Mode=Disable'

var secrets = [
  {
    name: 'extension-secret'
    keyVaultUrl: '${vault.properties.vaultUri}secrets/extension-secret'
    identity: identity.id
  }
  {
    name: 'webhook-password'
    keyVaultUrl: '${vault.properties.vaultUri}secrets/webhook-password'
    identity: identity.id
  }
]

var sharedEnv = concat(
  [
    { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
    { name: 'ConnectionStrings__Metadata', value: connectionString }
    { name: 'Database__UseManagedIdentity', value: 'true' }
    { name: 'AzureDevOps__Organization', value: adoOrganization }
    { name: 'AzureDevOps__UseManagedIdentity', value: 'true' }
    { name: 'AzureDevOps__ExtensionSecret', secretRef: 'extension-secret' }
    { name: 'Security__DataProtectionBlobUri', value: '${storage.properties.primaryEndpoints.blob}dataprotection/keys.xml' }
    { name: 'Security__DataProtectionKeyVaultKeyId', value: '${vault.properties.vaultUri}keys/dataprotection' }
    { name: 'Security__WebhookUsername', value: webhookUsername }
    { name: 'Security__WebhookPassword', secretRef: 'webhook-password' }
    { name: 'Admin__Users', value: chatAdmins }
    { name: 'Indexing__PollIntervalMinutes', value: string(pollIntervalMinutes) }
    { name: 'AzureOpenAI__UseManagedIdentity', value: 'true' }
    { name: 'AzureOpenAI__Endpoint', value: azureOpenAIEndpoint }
    { name: 'AzureOpenAI__ChatDeployment', value: azureOpenAIChatDeployment }
    { name: 'AzureOpenAI__EmbeddingDeployment', value: azureOpenAIEmbeddingDeployment }
  ],
  empty(extensionOrigin) ? [] : [{ name: 'Security__AllowedOrigins__0', value: extensionOrigin }]
)

var registries = [
  {
    server: registry.properties.loginServer
    identity: identity.id
  }
]

resource api 'Microsoft.App/containerApps@2024-03-01' = if (deployApps) {
  name: names.api
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
      registries: registries
      secrets: secrets
    }
    template: {
      containers: [
        {
          name: 'api'
          image: '${registry.properties.loginServer}/chat-with-code-api:${imageTag}'
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: sharedEnv
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/health/live', port: 8080 }
              periodSeconds: 30
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health/ready', port: 8080 }
              initialDelaySeconds: 10
              periodSeconds: 15
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
  dependsOn: [acrPull, vaultSecretsUser, vaultCryptoUser, blobContributor, allowAzure]
}

resource worker 'Microsoft.App/containerApps@2024-03-01' = if (deployApps) {
  name: names.worker
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: registries
      secrets: secrets
    }
    template: {
      containers: [
        {
          name: 'worker'
          image: '${registry.properties.loginServer}/chat-with-code-worker:${imageTag}'
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: sharedEnv
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
  // The API applies database migrations on start; deploying it first avoids a noisy first worker start.
  dependsOn: [api]
}

// ---------------- Outputs ----------------

output apiUrl string = deployApps ? 'https://${api!.properties.configuration.ingress.fqdn}' : ''
output registryName string = registry.name
output registryLoginServer string = registry.properties.loginServer
output keyVaultName string = vault.name
output identityName string = identity.name
output identityClientId string = identity.properties.clientId
output identityPrincipalId string = identity.properties.principalId
output postgresHost string = postgres.properties.fullyQualifiedDomainName
