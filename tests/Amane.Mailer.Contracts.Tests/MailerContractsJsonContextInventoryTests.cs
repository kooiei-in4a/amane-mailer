using System.Text.Json;
using System.Text.Json.Serialization;
using Amane.Mailer.Contracts.Json;
using Amane.Mailer.Contracts.MailRequests;

namespace Amane.Mailer.Contracts.Tests;

/// <summary>
/// Inventory of production types registered on <see cref="MailerContractsJsonContext"/>.
/// Fails CI when a Contracts DTO is added without a matching [JsonSerializable] entry.
/// </summary>
public sealed class MailerContractsJsonContextInventoryTests
{
    public static IEnumerable<object[]> ProductionTypes()
    {
        foreach (var type in AllProductionTypes)
        {
            yield return [type];
        }
    }

    // Keep in sync with [JsonSerializable] on MailerContractsJsonContext and
    // scripts/check-contract-drift.mjs Contracts JSON context list.
    private static readonly Type[] AllProductionTypes =
    [
        typeof(MailRequestCreateRequest),
        typeof(MailRequestCreateResponse),
        typeof(MailRequestStatusResponse),
        typeof(MailRequestRescheduleRequest),
        typeof(MailRecipientDto),
        typeof(MailRecipientDto[]),
        typeof(MailAttachmentDto),
        typeof(MailAttachmentDto[]),
        typeof(Dictionary<string, string>),
    ];

    [Theory]
    [MemberData(nameof(ProductionTypes))]
    public void GetTypeInfo_resolves_production_dto(Type type)
    {
        var typeInfo = MailerContractsJsonContext.Default.GetTypeInfo(type);

        Assert.NotNull(typeInfo);
        Assert.Equal(type, typeInfo.Type);
    }

    [Fact]
    public void Inventory_matches_JsonSerializable_attributes()
    {
        var registered = typeof(MailerContractsJsonContext)
            .GetCustomAttributesData()
            .Where(static data => data.AttributeType == typeof(JsonSerializableAttribute))
            .Select(static data => (Type)data.ConstructorArguments[0].Value!)
            .ToArray();

        Assert.Equal(
            AllProductionTypes.OrderBy(static t => t.FullName, StringComparer.Ordinal).ToArray(),
            registered.OrderBy(static t => t.FullName, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Untyped_serialize_rejects_unregistered_type_without_reflection_fallback()
    {
        // Verifies the source-generated context has no implicit reflection fallback for
        // unregistered types when it is the sole TypeInfoResolver. This does not exercise
        // JsonSerializerIsReflectionEnabledByDefault (entry-assembly runtimeconfig switch).
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = MailerContractsJsonContext.Default,
        };

        var unregistered = new UnregisteredInventoryProbe("value");

        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(unregistered, options));
    }

    [Fact]
    public void Typed_serialize_path_still_works_for_registered_dto()
    {
        var dto = new MailRecipientDto { Email = "user@example.com" };
        var json = JsonSerializer.Serialize(dto, MailerContractsJsonContext.Default.MailRecipientDto);

        Assert.Equal("""{"email":"user@example.com"}""", json);
    }

    private sealed record UnregisteredInventoryProbe(string Value);
}
