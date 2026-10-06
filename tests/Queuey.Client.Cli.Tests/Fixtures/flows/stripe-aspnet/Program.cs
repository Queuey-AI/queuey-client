var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddSingleton<StripeApi.OrderStore>();

var app = builder.Build();
app.MapControllers();
app.Run();
