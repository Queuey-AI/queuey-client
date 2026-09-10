var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<OrdersDb>();
var app = builder.Build();

app.MapPost("/orders", async (Order order, OrdersDb db) =>
{
    db.Orders.Add(order);
    await db.SaveChangesAsync();
    // TODO: notify partners that an order was created
    return Results.Created($"/orders/{order.Id}", order);
});

app.Run();

public record Order(Guid Id, string CustomerId, decimal Total);
public class OrdersDb : Microsoft.EntityFrameworkCore.DbContext
{
    public Microsoft.EntityFrameworkCore.DbSet<Order> Orders => Set<Order>();
}
