using System.Text.Json;
using System.Text.Json.Serialization;
using Amane.Mailer.Configuration;
using Amane.Mailer.Contracts.MailRequests;
using Amane.Mailer.Json;

namespace Amane.Mailer.Tests.Json;

/// <summary>
/// Inventory of production types registered on <see cref="MailerJsonContext"/>.
/// Fails CI when a production DTO is used without a matching [JsonSerializable] entry.
/// </summary>
public sealed class MailerJsonContextInventoryTests
{
    public static IEnumerable<object[]> ProductionTypes()
    {
        foreach (var type in AllProductionTypes)
        {
            yield return [type];
        }
    }

    // Keep in sync with [JsonSerializable] on MailerJsonContext and
    // scripts/check-contract-drift.mjs runtime JSON context list.
    private static readonly Type[] AllProductionTypes =
    [
        typeof(MailerErrorResponse),
        typeof(MailerValidationErrorResponse),
        typeof(MailerServiceUnavailableResponse),
        typeof(HealthStatusResponse),
        typeof(ReadyStatusResponse),
        typeof(MailerTenantsFile),
        typeof(MailerTenant),
        typeof(MailerAddress),
        typeof(MailerRetryOptions),
        typeof(MailerWebhookConfig),
        typeof(List<MailerTenant>),
        typeof(MailDeliveryEventPayload),
        typeof(PlatformSenderFile),
        typeof(PlatformSenderAddress),
        typeof(MailRequestCreateRequest),
        typeof(MailRequestCreateResponse),
        typeof(MailRequestStatusResponse),
        typeof(MailRequestRescheduleRequest),
        typeof(MailRecipientDto),
        typeof(MailRecipientDto[]),
        typeof(Dictionary<string, string>),
    ];

    [Theory]
    [MemberData(nameof(ProductionTypes))]
    public void GetTypeInfo_resolves_production_dto(Type type)
    {
        var typeInfo = MailerJsonContext.Default.GetTypeInfo(type);

        Assert.NotNull(typeInfo);
        Assert.Equal(type, typeInfo.Type);
    }

    [Fact]
    public void Inventory_matches_JsonSerializable_attributes()
    {
        var registered = typeof(MailerJsonContext)
            .GetCustomAttributesData()
            .Where(static data => data.AttributeType == typeof(JsonSerializableAttribute))
            .Select(static data => (Type)data.ConstructorArguments[0].Value!)
            .ToArray();

        Assert.Equal(SortTypes(AllProductionTypes), SortTypes(registered));
    }

    [Fact]
    public void Untyped_serialize_rejects_unregistered_type_without_reflection_fallback()
    {
        // Verifies the source-generated context has no implicit reflection fallback for
        // unregistered types when it is the sole TypeInfoResolver. This does not exercise
        // JsonSerializerIsReflectionEnabledByDefault (entry-assembly runtimeconfig switch).
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = MailerJsonContext.Default,
        };

        var unregistered = new UnregisteredInventoryProbe("value");

        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(unregistered, options));
    }

    [Fact]
    public void Typed_serialize_path_still_works_for_registered_dto()
    {
        var json = JsonSerializer.Serialize(
            new HealthStatusResponse(true),
            MailerJsonContext.Default.HealthStatusResponse);

        Assert.Equal("""{"healthy":true}""", json);
    }

    private static Type[] SortTypes(IEnumerable<Type> types) =>
        types.OrderBy(static t => t.FullName, StringComparer.Ordinal).ToArray();

    private sealed record UnregisteredInventoryProbe(string Value);
}
