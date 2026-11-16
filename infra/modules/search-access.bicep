// Gives the app identity what it needs on an existing Azure AI Search service: create the index
// (Search Service Contributor) and read/write documents (Search Index Data Contributor).
param serviceName string
param principalId string

resource search 'Microsoft.Search/searchServices@2024-06-01-preview' existing = {
  name: serviceName
}

var roleIds = [
  '7ca78c08-252a-4471-8644-bb5ff32d4ba0' // Search Service Contributor
  '8ebe5a00-799e-43f5-93ac-243d3dce84a7' // Search Index Data Contributor
]

resource assignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for roleId in roleIds: {
  name: guid(search.id, principalId, roleId)
  scope: search
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)
  }
}]
