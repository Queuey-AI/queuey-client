using MassTransit;
using Microsoft.EntityFrameworkCore;

public class BillingDbContext(DbContextOptions<BillingDbContext> o) : DbContext(o)
{
    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.AddInboxStateEntity();
        b.AddOutboxMessageEntity();
        b.AddOutboxStateEntity();
    }
}

public record Invoice(string InvoiceId, string CustomerId);
