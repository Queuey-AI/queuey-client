using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Queuey.Client;
using Queuey.Client.Waas;
using Queuey.Examples;

ExampleConfig config = ExampleConfig.FromEnvironment();

// Register the SDK: set credentials, declare the stream model(s), turn on schema generation.
var services = new ServiceCollection();
services.AddQueuey(config.Apply, b => b.AddStream<OrderCreated>().GenerateSchemas());
using ServiceProvider provider = services.BuildServiceProvider();
var queuey = provider.GetRequiredService<IQueueyService>();

Console.WriteLine("Queuey.Client — examples\n");
config.Print();

const string menu = """

    Pick an example:
      1) Onboarding        — create a tenant + queue
      2) Sync models       — apply streams + packages
      3) Publish an event
      4) Packages          — create / update / assign / archive
      5) Integrations      — invite / grant / activate a partner
      6) Observability     — queue metrics + issues
      7) Full walkthrough  — 2 → 3 → 5 → 6
      0) Quit
    """;

// Non-interactive one-shot: `dotnet run -- 7` runs one example and exits (handy for CI / a quick demo).
string? preset = args.Length > 0 ? args[0] : null;

while (true)
{
    Console.WriteLine(menu);
    Console.Write("> ");
    string? choice = preset ?? Console.ReadLine()?.Trim();
    Console.WriteLine();

    try
    {
        switch (choice)
        {
            case "1": await Examples.Onboarding(queuey); break;
            case "2": await Examples.Sync(queuey); break;
            case "3": await Examples.Publish(queuey); break;
            case "4": await Examples.Packages(queuey); break;
            case "5": await Examples.Integrations(queuey); break;
            case "6": await Examples.Observability(queuey, config); break;
            case "7": await Examples.FullWalkthrough(queuey, config); break;
            case "0" or "q" or "quit" or null: Console.WriteLine("Bye."); return 0;
            default: Console.WriteLine($"Unknown choice: {choice}"); break;
        }
    }
    catch (QueueyConfigurationException ex) { Console.Error.WriteLine($"Config error: {ex.Message}"); }
    catch (QueueyException ex) { Console.Error.WriteLine($"Queuey error: {ex.StatusCode} {ex.ErrorCode} — {ex.Message}"); }
    catch (HttpRequestException ex) { Console.Error.WriteLine($"Could not reach Queuey ({config.ApiBase} / {config.IngressBase}): {ex.Message}"); }

    if (preset is not null) return 0; // one-shot mode
}
