using System.Net;
using System.Text;
using System.Text.Json;
using Queuey.Client.Cli;

namespace Queuey.Client.Cli.Tests;

/// <summary>Hva en kommando skrev, med stdout og stderr hver for seg.</summary>
internal sealed record CliRun(int Exit, string Stdout, string Stderr);

/// <summary>
/// Kjører en kommando i prosessen. Utskriften fanges per strøm, fordi `--json` lover at svaret (også
/// en feil) står på stdout og ikke noe annet; og nettet byttes ut med en handler som husker hvert kall.
/// Brukes bare fra testklasser i <see cref="ConsoleCollection"/>, som ikke kjøres samtidig.
/// </summary>
internal static class CliHarness
{
    /// <summary>Tilkoblingsvalg som peker en kommando på testserveren i stedet for Queuey.</summary>
    public static readonly string[] Connection =
    {
        "--api-key", "qak_kid.secret", "--license", "lic_1",
        "--api-base", "https://api.test", "--ingress-base", "https://ingress.test",
        "--config", Path.Combine(Path.GetTempPath(), "queuey-cli-tests-no-config.json"),
    };

    public static string[] With(params string[] args) => args.Concat(Connection).ToArray();

    public static async Task<CliRun> RunAsync(Func<Task<int>> command, RecordingHandler? api = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        TextWriter originalOut = Console.Out, originalErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        CliHost.TestHandler = api ?? new RecordingHandler(_ => throw new InvalidOperationException("This test sends nothing."));
        try
        {
            int exit = await command();
            return new CliRun(exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            CliHost.TestHandler = null;
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}

/// <summary>Én forespørsel slik testserveren fikk den.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body)
{
    public string Path => Uri.AbsolutePath;
    public string Key => $"{Method.Method} {Uri.AbsolutePath}";
    public JsonElement Json => JsonDocument.Parse(Body!).RootElement;
}

/// <summary>En testserver: svarer med <c>respond</c> og husker hver forespørsel, med kropp.</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Func<RecordedRequest, HttpResponseMessage> _respond;

    public RecordingHandler(Func<RecordedRequest, HttpResponseMessage> respond) => _respond = respond;

    public List<RecordedRequest> Requests { get; } = new();

    public IEnumerable<RecordedRequest> Writes => Requests.Where(r => r.Method != HttpMethod.Get);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, string.IsNullOrEmpty(body) ? null : body);
        lock (Requests) Requests.Add(recorded);
        return _respond(recorded);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, object body)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage NoContent() => new(HttpStatusCode.NoContent);

    public static HttpResponseMessage Error(HttpStatusCode status, string code, string message, string? action = null)
        => Json(status, new { error = new { code, message, action } });
}
