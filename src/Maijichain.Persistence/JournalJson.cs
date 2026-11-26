using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Maijichain.Domain;

namespace Maijichain.Persistence;

/// <summary>Stable JSON contract for journal records. Discriminator names are part of the on-disk format.</summary>
public static class JournalJson
{
    private static readonly (Type Type, string Name)[] EventTypes =
    [
        (typeof(GroupOpened), "group.opened"),
        (typeof(MemberJoined), "member.joined"),
        (typeof(MemberRoleChanged), "member.role_changed"),
        (typeof(MemberDeactivated), "member.deactivated"),
        (typeof(ExpenseDrafted), "expense.drafted"),
        (typeof(DraftDiscarded), "expense.draft_discarded"),
        (typeof(ExpensePosted), "expense.posted"),
        (typeof(ExpenseReversed), "expense.reversed"),
        (typeof(PaymentClaimed), "payment.claimed"),
        (typeof(PaymentPosted), "payment.posted"),
        (typeof(PaymentRejected), "payment.rejected"),
        (typeof(PlanCreated), "plan.created"),
        (typeof(PeriodClosed), "period.closed"),
    ];

    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize(LedgerEvent e) => JsonSerializer.Serialize(e, Options);

    public static LedgerEvent Deserialize(string json) =>
        JsonSerializer.Deserialize<LedgerEvent>(json, Options) ?? throw new JournalCorruptedException("Empty journal record.");

    internal static JsonSerializerOptions Create(params (Type Base, (Type Type, string Name)[] Derived)[] extraHierarchies)
    {
        var hierarchies = extraHierarchies.Prepend((typeof(LedgerEvent), EventTypes)).ToArray();
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            foreach (var (baseType, derived) in hierarchies)
            {
                if (info.Type != baseType)
                {
                    continue;
                }

                info.PolymorphismOptions = new JsonPolymorphismOptions
                {
                    TypeDiscriminatorPropertyName = "$type",
                    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
                };
                foreach (var (type, name) in derived)
                {
                    info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(type, name));
                }
            }
        });

        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = resolver,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        options.Converters.Add(new MemberIdConverter());
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class MemberIdConverter : JsonConverter<MemberId>
    {
        public override MemberId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new(reader.GetString() ?? throw new JsonException("Member id is null."));

        public override void Write(Utf8JsonWriter writer, MemberId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    }
}
