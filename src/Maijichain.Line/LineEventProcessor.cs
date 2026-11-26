using System.Text.Json;
using Maijichain.Application;
using Maijichain.Domain;

namespace Maijichain.Line;

/// <summary>
/// Turns one accepted webhook event into at most one ledger command and one queued reply. Every command uses the
/// event's <c>webhookEventId</c> as its idempotency key, so reprocessing after a crash or redelivery has no second effect.
/// </summary>
public sealed class LineEventProcessor(LedgerService ledger, IOperationsStore ops, IdentityHasher ids, LineOptions options)
{
    private readonly CommandParser _parser = new(options.DefaultCurrency);

    public async Task<string> ProcessAsync(InboxItem item, CancellationToken ct)
    {
        LineEvent evt;
        try
        {
            evt = LineEvent.Parse(item.Payload);
        }
        catch (JsonException)
        {
            return "ignored:unparseable";
        }

        if (evt.ChatId is null)
        {
            if (evt.Type == "message" && evt.ReplyToken is not null)
            {
                Enqueue(item, evt, "direct", new Response(LineMessages.Text(Replies.GroupOnly), false, "direct-chat"), 1);
            }

            return "ignored:not-a-group";
        }

        ChatCommand? command = evt.Type switch
        {
            "message" when evt.MessageType == "text" => _parser.ParseText(evt.Text),
            "postback" => CommandParser.ParsePostback(evt.PostbackData),
            "join" => new HelpCommand(),
            _ => null, // unsend, memberJoined, stickers, …: nothing is stored, so nothing to undo
        };
        if (command is null)
        {
            return $"ignored:{evt.Type}";
        }

        string groupId = ids.GroupId(evt.ChatId);
        ops.RememberRoute(groupId, evt.ChatId);
        await ledger.OpenGroupAsync(groupId, "LINE group", options.DefaultCurrency, options.RemainderPolicy, $"open:{item.EventId}", ct).ConfigureAwait(false);

        Response response;
        try
        {
            string? identity = evt.UserId is null ? null : ids.UserRef(evt.UserId);
            response = await ExecuteAsync(command, groupId, identity, item.EventId, ct).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            response = new Response(LineMessages.Text(Replies.Error(ex)), false, $"rejected:{ex.Code}");
        }

        int audience = await ledger.QueryAsync(groupId, g => g.ActiveMembers.Count, ct).ConfigureAwait(false);
        Enqueue(item, evt, groupId, response, Math.Max(audience, 1));
        return response.Outcome;
    }

    private void Enqueue(InboxItem item, LineEvent evt, string groupId, Response response, int audience) =>
        ops.EnqueueOutbound(new OutboxItem($"{item.EventId}:reply", groupId, evt.ReplyToken, item.ReceivedAt, response.Payload, response.Important, audience));

    private async Task<Response> ExecuteAsync(ChatCommand command, string groupId, string? identity, string key, CancellationToken ct)
    {
        switch (command)
        {
            case HelpCommand:
                return Plain(Replies.Help, "help");
            case InvalidCommand invalid:
                return Plain(Replies.Invalid(invalid), "invalid");
            case JoinCommand join:
                {
                    string who = identity ?? throw new DomainException(ErrorCodes.UnknownMember, "LINE did not share your user ID. Please allow profile access, then try again.");
                    return await Run(groupId, key, (g, ctx) => g.Join(who, join.Nickname, ctx), (g, d) => Plain(Replies.Joined(g, (MemberJoined)d.Event, d.IsDuplicate), "joined"), ct).ConfigureAwait(false);
                }

            case BalanceCommand bal:
                return await ledger.QueryAsync(groupId, g => Plain(bal.Nickname is null ? Replies.Balances(g) : Replies.Statement(g, g.FindByNickname(bal.Nickname) ?? throw UnknownNick(bal.Nickname)), "balances"), ct).ConfigureAwait(false);
            case HistoryCommand history:
                return await ledger.QueryAsync(groupId, g => Plain(Replies.History(g, history.Count), "history"), ct).ConfigureAwait(false);
        }

        return await Run(
            groupId,
            key,
            (g, ctx) => Decide(g, command, Actor(g, identity), ctx),
            (g, d) => Render(g, d),
            ct).ConfigureAwait(false);
    }

    private Decision Decide(GroupLedger g, ChatCommand command, MemberId actor, CommandContext ctx) => command switch
    {
        AddExpenseCommand add => g.ProposeExpense(
            actor,
            add.PayerNickname is null ? actor : Resolve(g, add.PayerNickname, actor),
            add.Amount,
            add.Description,
            add.OccurredOn ?? DateOnly.FromDateTime(ctx.Now.ToOffset(TimeSpan.FromHours(options.UtcOffsetHours)).DateTime),
            BuildSplit(g, add, actor),
            ctx),
        ConfirmCommand c when c.Id.StartsWith('D') => g.ConfirmDraft(actor, c.Id, ctx),
        ConfirmCommand c => g.ConfirmPayment(actor, c.Id, ctx),
        CancelCommand c => g.DiscardDraft(actor, c.DraftId, "cancelled", ctx),
        RejectCommand r => g.RejectPayment(actor, r.PaymentId, r.Reason, ctx),
        UndoCommand u => g.ReverseExpense(actor, u.ExpenseId, u.Reason, ctx),
        PlanCommand => g.CreatePlan(actor, ctx),
        PaidCommand p => g.ClaimPayment(actor, Resolve(g, p.ToNickname, actor), p.Amount, ctx),
        PaidPlanTransferCommand p => g.ClaimPlanTransfer(actor, p.PlanId, p.TransferIndex, ctx),
        GotCommand got => g.RecordReceipt(actor, Resolve(g, got.FromNickname, actor), got.Amount, ctx),
        CloseCommand close => g.ClosePeriod(actor, close.Label, ctx),
        _ => throw new InvalidOperationException($"Unhandled command {command.GetType().Name}."),
    };

    private Response Render(GroupLedger g, Decision d) => d.Event switch
    {
        ExpenseDrafted x => new Response(
            LineMessages.TextWithChoice(
                Replies.Preview(g, x, options.UtcOffsetHours),
                $"Post {x.DraftId}: {x.Description} {g.Currency.Format(x.Amount)}?",
                new LineMessages.Button("Confirm", $"a=confirm&id={x.DraftId}", $"Confirm {x.DraftId}"),
                new LineMessages.Button("Cancel", $"a=cancel&id={x.DraftId}", $"Cancel {x.DraftId}")),
            false,
            "drafted"),
        ExpensePosted x => Important(Replies.Posted(g, x, d.IsDuplicate), d.IsDuplicate ? "duplicate" : "posted"),
        DraftDiscarded x => Plain($"{x.DraftId} cancelled. Nothing was recorded.", "discarded"),
        ExpenseReversed x => Important(Replies.Reversed(g, x), "reversed"),
        PaymentClaimed x => new Response(
            LineMessages.TextWithChoice(
                Replies.Claimed(g, x),
                $"{Replies.Name(g, x.To)}: did you receive {g.Currency.Format(x.Amount)} from {Replies.Name(g, x.From)}?",
                new LineMessages.Button("Received", $"a=confirm&id={x.PaymentId}", $"Received {x.PaymentId}"),
                new LineMessages.Button("Not received", $"a=reject&id={x.PaymentId}", $"Not received {x.PaymentId}")),
            true,
            "payment-claimed"),
        PaymentPosted x => Important(Replies.PaymentRecorded(g, x, d.IsDuplicate), d.IsDuplicate ? "duplicate" : "payment-posted"),
        PaymentRejected x => Important(Replies.PaymentRefused(g, x), "payment-rejected"),
        PlanCreated x => Plain(Replies.Plan(g, x), "plan"),
        PeriodClosed x => Important(Replies.Closed(x), "period-closed"),
        _ => Plain("Done.", d.Event.GetType().Name),
    };

    private async Task<Response> Run(string groupId, string key, Func<GroupLedger, CommandContext, Decision> decide, Func<GroupLedger, Decision, Response> render, CancellationToken ct) =>
        (await ledger.ExecuteAsync(groupId, decide, render, key, ct).ConfigureAwait(false)).Value;

    private static SplitRule BuildSplit(GroupLedger g, AddExpenseCommand add, MemberId actor)
    {
        var specs = add.Participants;
        if (specs.Count == 0 || (specs.Count == 1 && specs[0].Nickname.Equals("all", StringComparison.OrdinalIgnoreCase)))
        {
            return new EqualSplit(g.ActiveMembers.Select(m => m.Id).ToList());
        }

        if (specs.All(s => s.Kind == ParticipantKind.Exact))
        {
            return new ExactSplit(specs.Select(s => new Share(Resolve(g, s.Nickname, actor), s.Value)).ToList());
        }

        if (specs.Any(s => s.Kind == ParticipantKind.Weight))
        {
            return new WeightedSplit(specs.Select(s => (Resolve(g, s.Nickname, actor), (int)s.Value)).ToList());
        }

        return new EqualSplit(specs.Select(s => Resolve(g, s.Nickname, actor)).ToList());
    }

    private static MemberId Actor(GroupLedger g, string? identity) =>
        (identity is null ? null : g.FindByIdentity(identity))?.Id ?? throw new DomainException(ErrorCodes.UnknownMember, Replies.JoinFirst);

    private static MemberId Resolve(GroupLedger g, string nickname, MemberId actor) =>
        nickname.Equals("me", StringComparison.OrdinalIgnoreCase) ? actor : g.FindByNickname(nickname)?.Id ?? throw UnknownNick(nickname);

    private static DomainException UnknownNick(string nickname) => new(ErrorCodes.UnknownMember, $"No active member called @{nickname}.");

    private static Response Plain(string text, string outcome) => new(LineMessages.Text(text), false, outcome);

    private static Response Important(string text, string outcome) => new(LineMessages.Text(text), true, outcome);

    private sealed record Response(string Payload, bool Important, string Outcome);
}
