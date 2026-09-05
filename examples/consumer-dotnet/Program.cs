using System.Net;
using System.Net.Http.Json;
using Amane.Mailer.Contracts.Json;
using Amane.Mailer.Contracts.MailRequests;

var options = SampleOptions.Parse(args);

var mailRequestId = options.RequestId ?? Guid.NewGuid();

var request = new MailRequestCreateRequest
{
    MailRequestId = mailRequestId,
    Purpose = "FormResponseNotification",
    To = [new MailRecipientDto { Email = options.RecipientEmail }],
    Subject = options.Mutate ? "New response (edited)" : "New response",
    TextBody = "A new response arrived.",
};

Console.WriteLine($"POST {options.MailerBaseUrl}api/mail-requests");
Console.WriteLine($"mail_request_id: {mailRequestId}");
Console.WriteLine();

using var httpClient = new HttpClient { BaseAddress = new Uri(options.MailerBaseUrl) };
httpClient.DefaultRequestHeaders.Authorization =
    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.MailServiceToken);

using var response = await httpClient.PostAsJsonAsync(
    "api/mail-requests",
    request,
    MailerContractsJsonContext.Default.MailRequestCreateRequest);

var body = await response.Content.ReadAsStringAsync();

if (response.StatusCode == HttpStatusCode.Accepted)
{
    var result = System.Text.Json.JsonSerializer.Deserialize(
        body,
        MailerContractsJsonContext.Default.MailRequestCreateResponse)!;

    Console.WriteLine($"HTTP 202 Accepted — status: {result.Status}");

    if (result.Status == MailRequestAcceptanceStatus.AlreadyAccepted)
    {
        Console.WriteLine("This mail_request_id was already accepted with the same canonical payload;");
        Console.WriteLine("the Mailer treated this POST as an idempotent resend, not a new request.");
    }
}
else if (response.StatusCode == HttpStatusCode.Conflict)
{
    Console.WriteLine($"HTTP 409 Conflict: {body}");
    Console.WriteLine();
    Console.WriteLine($"This means mail_request_id {mailRequestId} was already accepted with a");
    Console.WriteLine("different payload. Reusing a mail_request_id after changing subject,");
    Console.WriteLine($"body, recipients, or metadata triggers {MailerErrorCodes.IdempotencyConflict}.");
    Console.WriteLine("Use a new mail_request_id for a genuinely new mail request.");
}
else
{
    Console.WriteLine($"HTTP {(int)response.StatusCode} {response.StatusCode}: {body}");
}

internal sealed record SampleOptions(
    string MailerBaseUrl,
    string MailServiceToken,
    string RecipientEmail,
    Guid? RequestId,
    bool Mutate)
{
    public static SampleOptions Parse(string[] args)
    {
        Guid? requestId = null;
        var mutate = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--request-id" when i + 1 < args.Length:
                    requestId = Guid.Parse(args[++i]);
                    break;
                case "--mutate":
                    mutate = true;
                    break;
            }
        }

        return new SampleOptions(
            MailerBaseUrl: Environment.GetEnvironmentVariable("MAILER_BASE_URL") ?? "http://127.0.0.1:5280/",
            MailServiceToken: Environment.GetEnvironmentVariable("MAILER_API_KEY")
                ?? throw new InvalidOperationException("MAILER_API_KEY must contain a managed API key."),
            RecipientEmail: Environment.GetEnvironmentVariable("MAILER_RECIPIENT_EMAIL") ?? "admin@example.com",
            RequestId: requestId,
            Mutate: mutate);
    }
}
