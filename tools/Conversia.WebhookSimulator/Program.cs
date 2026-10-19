using System.Net;
using Conversia.WebhookSimulator.Api;
using Conversia.WebhookSimulator.Cli;
using Conversia.WebhookSimulator.Commands;

namespace Conversia.WebhookSimulator;

public static class Program
{
    private const string Help = """
        Conversia webhook simulator: a development tool that fills a LOCAL Conversia API with believable WhatsApp conversations.

        Usage:
          simulator setup    [--api-url URL] [--print-secret] [--rotate-secret]
          simulator play     [--api-url URL] [--scenario booking|price-objection|unanswered|ad-origin|bsuid|duplicate|out-of-order|all] [--branch NAME] [--seed N]
          simulator populate [--api-url URL] [--conversations 300] [--days 30] [--seed 42] [--previous-share 0.3] [--rate 15] [--wait-seconds 180] [--no-assign]

        Common options:
          --api-url URL     The API (default $SIM_API_URL or http://localhost:5030). Only local addresses unless --allow-remote.
          --allow-remote    Accept a non-local API address.

        Environment (never the command line):
          SIM_EMAIL, SIM_PASSWORD   Login of a user of the workspace (the owner).
          SIM_WEBHOOK_SECRETS       'Centro=<secret>;Norte=<secret>;...' one secret per number (setup --print-secret shows them once).
          SIM_WEBHOOK_SECRET        One secret to try for a branch that is not listed above.

        Details: docs/SIMULATOR.md
        """;

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var environment = SimulatorEnvironment.FromProcess();
            var options = SimulatorOptions.Parse(args, environment.ApiUrl);
            if (options.Command == SimulatorCommandName.Help)
            {
                Console.Out.WriteLine(Help);
                return 0;
            }

            var apiUrl = ApiUrlGuard.Check(options.ApiUrl, options.AllowRemote);
            var (email, password) = environment.Credentials();

            // No redirects: an answer that sends the simulator elsewhere must not take the token or a webhook secret along.
            using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { BaseAddress = apiUrl, Timeout = TimeSpan.FromSeconds(60) };
            var api = new ConversiaApi(http);

            // Webhook deliveries get a client of their own: the owner's token (set on `http` by the login) must never ride along with a
            // channel secret, and a webhook is a public route that wants only that secret.
            using var webhookHandler = new SocketsHttpHandler { AllowAutoRedirect = false };
            using var webhookHttp = new HttpClient(webhookHandler) { BaseAddress = apiUrl, Timeout = TimeSpan.FromSeconds(60) };
            await api.LoginAsync(email, password, cancellation.Token);

            return options.Command switch
            {
                SimulatorCommandName.Setup => await new SetupCommand(api, Console.Out).RunAsync(options, cancellation.Token),
                SimulatorCommandName.Play => await new PlayCommand(api, new WebhookSender(webhookHttp, options.Rate), environment, TimeProvider.System, Console.Out).RunAsync(options, cancellation.Token),
                _ => await new PopulateCommand(api, new WebhookSender(webhookHttp, options.Rate), environment, TimeProvider.System, Console.Out).RunAsync(options, cancellation.Token),
            };
        }
        catch (UsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (ApiFailure exception)
        {
            Console.Error.WriteLine(exception.Message);
            return exception.Status == HttpStatusCode.Unauthorized ? 3 : 1;
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine("Could not reach the API. Is it running, and is --api-url right?");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            // The type only: the text of an exception can quote an address that holds a webhook key.
            Console.Error.WriteLine($"Unexpected failure ({exception.GetType().Name}). Run again; if it repeats, that is a bug of the simulator.");
            return 1;
        }
    }
}
