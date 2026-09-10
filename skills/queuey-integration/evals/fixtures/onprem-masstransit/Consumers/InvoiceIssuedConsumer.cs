using MassTransit;

public sealed class InvoiceIssuedConsumer(BillingDbContext db) : IConsumer<InvoiceIssued>
{
    public async Task Consume(ConsumeContext<InvoiceIssued> context)
    {
        // Runs inside the outbox scope: the message is only acked once the
        // transaction that wrote the invoice commits.
        db.Invoices.Add(new Invoice(context.Message.InvoiceId, context.Message.CustomerId));
        await db.SaveChangesAsync(context.CancellationToken);
        // External subscribers would be notified from here.
    }
}

public record InvoiceIssued(string InvoiceId, string CustomerId, decimal Amount);
