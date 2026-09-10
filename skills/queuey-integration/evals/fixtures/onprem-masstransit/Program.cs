var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService();
builder.Services.AddDbContext<BillingDbContext>();
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<InvoiceIssuedConsumer>();
    x.AddEntityFrameworkOutbox<BillingDbContext>(o =>
    {
        o.UseSqlServer();
        o.UseBusOutbox();
    });
    x.UsingRabbitMq((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
});
builder.Build().Run();
