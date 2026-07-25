using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Amane.Mailer.Tests.Api;

internal static class MailRequestHttpResultAssertions
{
    public static (int StatusCode, string Body) Inspect(IResult result)
    {
        var statusCode = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode
            ?? throw new InvalidOperationException("IResult did not expose a status code.");
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        var body = value is null
            ? string.Empty
            : JsonSerializer.Serialize(value);
        return (statusCode, body);
    }

    public static T Value<T>(IResult result)
    {
        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
        return Assert.IsType<T>(valueResult.Value);
    }
}
