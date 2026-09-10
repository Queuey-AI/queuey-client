resource ordersApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'orders-api'
  properties: {
    template: {
      containers: [ { name: 'orders-api', image: 'acr.azurecr.io/orders-api:latest' } ]
      scale: { minReplicas: 0, maxReplicas: 5 }
      // no volumes: the container filesystem is discarded on every restart
    }
  }
}
