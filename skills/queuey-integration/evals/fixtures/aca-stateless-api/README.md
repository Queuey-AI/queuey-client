# Orders API

Minimal API that accepts orders and stores them in Azure SQL. Deployed to Azure
Container Apps from `infra/orders.bicep`; scales to zero outside business hours.
Partners are currently emailed a daily CSV; we want per-order notifications.
