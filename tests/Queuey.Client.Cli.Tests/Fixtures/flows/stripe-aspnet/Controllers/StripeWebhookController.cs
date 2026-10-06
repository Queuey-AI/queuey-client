using Microsoft.AspNetCore.Mvc;
using Stripe;

namespace StripeApi.Controllers;

[ApiController]
[Route("api/stripe")]
public class StripeWebhookController : ControllerBase
{
    private readonly string _webhookSecret;
    private readonly OrderStore _orders;

    public StripeWebhookController(IConfiguration configuration, OrderStore orders)
    {
        _webhookSecret = configuration["Stripe:WebhookSecret"]!;
        _orders = orders;
    }

    [HttpPost]
    public async Task<IActionResult> Receive()
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
        var stripeEvent = EventUtility.ConstructEvent(json, Request.Headers["Stripe-Signature"], _webhookSecret);

        if (await _orders.HasProcessedEventAsync(stripeEvent.Id))
            return Ok();

        switch (stripeEvent.Type)
        {
            case "checkout.session.completed":
                await _orders.MarkPaidAsync(stripeEvent);
                break;
            case "invoice.paid":
                await _orders.ExtendSubscriptionAsync(stripeEvent);
                break;
        }

        await _orders.RememberProcessedEventAsync(stripeEvent.Id);
        return Ok();
    }
}
