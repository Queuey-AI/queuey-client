# Billing service

Runs as a Windows service on a VM in our datacenter (Hyper-V, local SSD).
Consumes commands from RabbitMQ through MassTransit, writes invoices to SQL
Server, and publishes integration events through the MassTransit EF Core
outbox. Customers have asked to subscribe to invoice events externally.
