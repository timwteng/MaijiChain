using System.Security.Cryptography;
using System.Text;
using Maijichain.Domain;
using Maijichain.Line;

namespace Maijichain.Integration.Tests;

public sealed class LineSignatureTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"destination\":\"U1\",\"events\":[]}");

    private static string Reference(byte[] body, string secret) =>
        Convert.ToBase64String(new HMACSHA256(Encoding.UTF8.GetBytes(secret)).ComputeHash(body)); // independent oracle

    [Fact]
    public void Accepts_signature_over_exact_raw_body() =>
        Assert.True(LineSignature.IsValid(Body, Reference(Body, "s3cret"), "s3cret"));

    [Fact]
    public void Rejects_reformatted_body_even_if_json_is_equivalent()
    {
        byte[] pretty = Encoding.UTF8.GetBytes("{ \"destination\": \"U1\", \"events\": [] }");
        Assert.False(LineSignature.IsValid(pretty, Reference(Body, "s3cret"), "s3cret"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void Rejects_missing_or_malformed_headers(string? header) => Assert.False(LineSignature.IsValid(Body, header, "s3cret"));

    [Fact]
    public void Rejects_wrong_secret() => Assert.False(LineSignature.IsValid(Body, Reference(Body, "other"), "s3cret"));
}

public sealed class CommandParserTests
{
    private readonly CommandParser _parser = new(Currency.Twd);

    [Theory]
    [InlineData("hello everyone")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("/")]
    public void Ordinary_chat_is_not_a_command(string text) => Assert.Null(_parser.ParseText(text));

    [Fact]
    public void Add_with_defaults_splits_with_everyone()
    {
        var add = Assert.IsType<AddExpenseCommand>(_parser.ParseText("/add 1,200 hot pot dinner"));
        Assert.Equal(1200, add.Amount);
        Assert.Equal("hot pot dinner", add.Description);
        Assert.Empty(add.Participants);
        Assert.Null(add.PayerNickname);
    }

    [Fact]
    public void Add_supports_participants_payer_and_date()
    {
        var add = Assert.IsType<AddExpenseCommand>(_parser.ParseText("/add 900 taxi @amy @bob by:@cat date:2026-11-18"));
        Assert.Equal(new[] { "amy", "bob" }, add.Participants.Select(p => p.Nickname).ToArray());
        Assert.Equal("cat", add.PayerNickname);
        Assert.Equal(new DateOnly(2026, 11, 18), add.OccurredOn);
    }

    [Fact]
    public void Add_supports_exact_and_weighted_forms_and_full_width_symbols()
    {
        var exact = Assert.IsType<AddExpenseCommand>(_parser.ParseText("／add 1000 hotel ＠amy=600 @bob=400"));
        Assert.All(exact.Participants, p => Assert.Equal(ParticipantKind.Exact, p.Kind));
        Assert.Equal(600, exact.Participants[0].Value);

        var weighted = Assert.IsType<AddExpenseCommand>(_parser.ParseText("/記 900 披薩 @amy*2 @bob"));
        Assert.Equal(ParticipantKind.Weight, weighted.Participants[0].Kind);
        Assert.Equal(ParticipantKind.Equal, weighted.Participants[1].Kind);
        Assert.Equal("披薩", weighted.Description);
    }

    [Theory]
    [InlineData("/add")]
    [InlineData("/add 300")]
    [InlineData("/add abc dinner")]
    [InlineData("/add 12.5 dinner")]
    [InlineData("/add 300 dinner @amy=100 @bob")]
    [InlineData("/add 300 dinner @all @amy")]
    [InlineData("/add 300 dinner date:20-11-2026")]
    [InlineData("/add 300 dinner @amy*0")]
    [InlineData("/frobnicate")]
    [InlineData("/confirm X1")]
    [InlineData("/undo E3")]
    public void Malformed_commands_explain_usage(string text) => Assert.IsType<InvalidCommand>(_parser.ParseText(text));

    [Fact]
    public void Payment_and_review_commands()
    {
        Assert.Equal(new PaidPlanTransferCommand("S2", 0), _parser.ParseText("/paid s2-1"));
        Assert.Equal(new PaidCommand(300, "amy"), _parser.ParseText("/paid 300 @amy"));
        Assert.Equal(new GotCommand(300, "bob"), _parser.ParseText("/got @bob 300"));
        Assert.Equal(new ConfirmCommand("D12"), _parser.ParseText("/confirm d12"));
        Assert.Equal(new RejectCommand("P3", "wrong amount"), _parser.ParseText("/reject P3 wrong amount"));
        Assert.Equal(new UndoCommand("E7", "entered twice"), _parser.ParseText("/undo E7 entered twice"));
        Assert.Equal(new BalanceCommand("amy"), _parser.ParseText("/bal @amy"));
        Assert.Equal(new HistoryCommand(5), _parser.ParseText("/history 5"));
        Assert.IsType<HelpCommand>(_parser.ParseText("/說明"));
    }

    [Fact]
    public void Postbacks_map_to_commands()
    {
        Assert.Equal(new ConfirmCommand("D3"), CommandParser.ParsePostback("a=confirm&id=D3"));
        Assert.Equal(new CancelCommand("D3"), CommandParser.ParsePostback("a=cancel&id=d3"));
        Assert.Equal(new RejectCommand("P4", "not received"), CommandParser.ParsePostback("a=reject&id=P4"));
        Assert.Equal(new PaidPlanTransferCommand("S1", 1), CommandParser.ParsePostback("a=pay&id=S1-2"));
    }

    [Theory]
    [InlineData("a=confirm&id=E1")]
    [InlineData("garbage")]
    [InlineData("")]
    public void Unknown_postbacks_are_ignored(string data) => Assert.Null(CommandParser.ParsePostback(data));
}

public sealed class WebhookReceiverTests : IDisposable
{
    private readonly Pipeline _p = new();

    public void Dispose() => _p.Dispose();

    [Fact]
    public void Invalid_signature_is_rejected_before_anything_is_stored()
    {
        byte[] body = Webhooks.Bytes(Webhooks.Body(Webhooks.Text("Uamy", "/help")));
        var receipt = _p.Receiver.Receive(body, LineSignature.Sign(body, "wrong-secret"));
        Assert.Equal(WebhookStatus.Unauthorized, receipt.Status);
        Assert.Empty(_p.Ops.PendingInbound());
    }

    [Fact]
    public void Verification_webhook_with_no_events_is_accepted()
    {
        var receipt = _p.Post();
        Assert.Equal(new WebhookReceipt(WebhookStatus.Accepted, 0, 0), receipt);
    }

    [Fact]
    public void TC005_redelivered_event_is_an_idempotent_success()
    {
        string id = Webhooks.NextId();
        Assert.Equal(1, _p.Post(Webhooks.Text("Uamy", "/help", id)).Accepted);
        var again = _p.Post(Webhooks.Text("Uamy", "/help", id, redelivery: true));
        Assert.Equal(new WebhookReceipt(WebhookStatus.Accepted, 0, 1), again);
        Assert.Single(_p.Ops.PendingInbound());
    }

    [Fact]
    public void Malformed_payloads_are_bad_requests()
    {
        foreach (string body in new[] { "not json", "{\"events\":{}}", "{\"events\":[{\"type\":\"message\"}]}" })
        {
            byte[] bytes = Webhooks.Bytes(body);
            Assert.Equal(WebhookStatus.BadRequest, _p.Receiver.Receive(bytes, LineSignature.Sign(bytes, Webhooks.Secret)).Status);
        }
    }

    [Fact]
    public void Destination_must_match_when_bot_id_is_configured()
    {
        _p.Options.BotUserId = "Uexpected";
        Assert.Equal(WebhookStatus.Unauthorized, _p.Post(Webhooks.Text("Uamy", "/help")).Status);
    }
}
