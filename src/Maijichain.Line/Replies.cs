using System.Globalization;
using System.Text;
using System.Text.Json;
using Maijichain.Domain;

namespace Maijichain.Line;

/// <summary>Builds LINE message-object arrays (the <c>messages</c> field of the reply/push APIs).</summary>
public static class LineMessages
{
    private const int MaxText = 5000;
    private const int MaxTemplateText = 240;

    public static string Text(string text) => JsonSerializer.Serialize(new object[] { TextObject(text) });

    /// <summary>A text message followed by a two-button confirm template whose buttons send postbacks.</summary>
    public static string TextWithChoice(string text, string question, Button yes, Button no) =>
        JsonSerializer.Serialize(new object[]
        {
            TextObject(text),
            new
            {
                type = "template",
                altText = Clip(question, 400),
                template = new { type = "confirm", text = Clip(question, MaxTemplateText), actions = new[] { Action(yes), Action(no) } },
            },
        });

    private static object TextObject(string text) => new { type = "text", text = Clip(text, MaxText) };

    private static object Action(Button b) => new { type = "postback", label = Clip(b.Label, 20), data = b.Data, displayText = Clip(b.DisplayText, 300) };

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public sealed record Button(string Label, string Data, string DisplayText);
}

/// <summary>User-facing wording, kept in one place so it can be translated without touching logic.</summary>
public static class Replies
{
    public const string JoinFirst = "Please join first: /join <nickname>";

    public const string GroupOnly = "Maijichain works inside a group chat. Add me to your group and send /help there.";

    public const string Help =
        "Maijichain commands\n" +
        "/join <nickname> – register yourself\n" +
        "/add 900 dinner – split equally with everyone\n" +
        "/add 900 dinner @amy @bob – split among some\n" +
        "/add 1000 hotel @amy=600 @bob=400 – exact amounts\n" +
        "/add 900 pizza @amy*2 @bob – by weight\n" +
        "   options: by:@payer  date:2026-11-20\n" +
        "/bal – balances   /bal @amy – how it adds up\n" +
        "/plan – fewest transfers to settle\n" +
        "/paid S2-1 or /paid 300 @amy – you paid someone\n" +
        "/got 300 @bob – you received money\n" +
        "/undo E7 <reason> – reverse an expense\n" +
        "/history – recent activity";

    public static string Name(GroupLedger g, MemberId id) => "@" + g.GetMember(id).Nickname;

    public static string Signed(GroupLedger g, long amount) =>
        (amount > 0 ? "+" : amount < 0 ? "−" : string.Empty) + g.Currency.Format(Math.Abs(amount));

    public static string Joined(GroupLedger g, MemberJoined e, bool repeat) =>
        repeat ? $"@{e.Nickname} is already registered."
        : $"Welcome @{e.Nickname}!" + (e.Role == MemberRole.Coordinator ? " You are the group coordinator." : string.Empty) + $" {g.ActiveMembers.Count} member(s) so far.";

    public static string Preview(GroupLedger g, ExpenseDrafted d, int utcOffsetHours)
    {
        var expires = d.ExpiresAt.ToOffset(TimeSpan.FromHours(utcOffsetHours));
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{d.DraftId} · {d.Description} · {g.Currency.Format(d.Amount)}\n");
        sb.Append(CultureInfo.InvariantCulture, $"Paid by {Name(g, d.Payer)} on {d.OccurredOn:yyyy-MM-dd} · {d.SplitSummary}\n");
        sb.Append(string.Join(" · ", d.Shares.Select(s => $"{Name(g, s.Member)} {g.Currency.Format(s.Amount)}")));
        sb.Append(CultureInfo.InvariantCulture, $"\nNothing changes until confirmed. Expires {expires:yyyy-MM-dd HH:mm} (UTC{utcOffsetHours:+0;-0}).");
        return sb.ToString();
    }

    public static string Posted(GroupLedger g, ExpensePosted e, bool repeat)
    {
        var x = g.GetExpense(e.ExpenseId);
        string head = repeat ? $"{x.DraftId} was already posted as {x.Id}." : $"{x.Id} posted: {x.Description} {g.Currency.Format(x.Amount)}.";
        var involved = x.Lines.Select(l => l.Member).Distinct().OrderBy(m => m);
        return head + "\nNow: " + string.Join(" · ", involved.Select(m => $"{Name(g, m)} {Signed(g, g.Balances[m])}"));
    }

    public static string Reversed(GroupLedger g, ExpenseReversed e) =>
        $"{e.ExpenseId} reversed ({e.Reason}). The original stays in history; its effect is cancelled.";

    public static string Claimed(GroupLedger g, PaymentClaimed p) =>
        $"{p.PaymentId}: {Name(g, p.From)} says they paid {Name(g, p.To)} {g.Currency.Format(p.Amount)}.\n" +
        $"{Name(g, p.To)}, please confirm. Balances change only after you confirm.";

    public static string PaymentRecorded(GroupLedger g, PaymentPosted p, bool repeat) =>
        (repeat ? $"{p.PaymentId} was already recorded. " : $"{p.PaymentId} recorded: ") +
        $"{Name(g, p.From)} → {Name(g, p.To)} {g.Currency.Format(p.Amount)}.\n" +
        $"Now: {Name(g, p.From)} {Signed(g, g.Balances[p.From])} · {Name(g, p.To)} {Signed(g, g.Balances[p.To])}";

    public static string PaymentRefused(GroupLedger g, PaymentRejected r)
    {
        var p = g.GetPayment(r.PaymentId);
        return $"{r.PaymentId} rejected by {Name(g, p.To)} ({r.Reason}). Balances unchanged.";
    }

    public static string Balances(GroupLedger g)
    {
        var rows = g.Members.Where(m => m.Active || g.Balances[m.Id] != 0)
            .Select(m => (m, b: g.Balances[m.Id]))
            .OrderByDescending(r => r.b).ThenBy(r => r.m.Id)
            .Select(r => $"@{r.m.Nickname} {Signed(g, r.b)}" + (r.b > 0 ? " (gets back)" : r.b < 0 ? " (owes)" : string.Empty));
        string pending = g.PendingPayments.Count == 0 ? string.Empty : $"\n{g.PendingPayments.Count} payment(s) await confirmation.";
        return "Balances\n" + string.Join('\n', rows) + pending;
    }

    public static string Statement(GroupLedger g, Member m)
    {
        var lines = g.StatementFor(m.Id);
        var recent = lines.Skip(Math.Max(0, lines.Count - 12)).Select(l => $"#{l.Sequence} {l.ReferenceId} {Kind(l.Kind)} {Signed(g, l.Amount)} → {Signed(g, l.RunningBalance)}");
        return $"@{m.Nickname}: {Signed(g, g.Balances[m.Id])}\n" + (lines.Count > 12 ? $"(last 12 of {lines.Count} lines)\n" : string.Empty) + string.Join('\n', recent);
    }

    public static string Plan(GroupLedger g, PlanCreated p)
    {
        if (p.Transfers.Count == 0)
        {
            return "Everyone is settled. Nothing to pay.";
        }

        string quality = p.Method == NettingMethod.ExactMinimum ? "the minimum possible" : "simplified (not proven minimal)";
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{p.PlanId}: {p.Transfers.Count} transfer(s), {quality}\n");
        for (int i = 0; i < p.Transfers.Count; i++)
        {
            var t = p.Transfers[i];
            sb.Append(CultureInfo.InvariantCulture, $"{i + 1}. {Name(g, t.From)} → {Name(g, t.To)} {g.Currency.Format(t.Amount)}  (/paid {p.PlanId}-{i + 1})\n");
        }

        sb.Append("Payments count only after the receiver confirms.");
        return sb.ToString();
    }

    public static string History(GroupLedger g, int count)
    {
        var items = g.Events.Where(e => e is ExpensePosted or ExpenseReversed or PaymentPosted or PeriodClosed).TakeLast(count).Select(e => e switch
        {
            ExpensePosted x => $"#{x.Sequence} {x.ExpenseId} {g.GetExpense(x.ExpenseId).Description} {g.Currency.Format(g.GetExpense(x.ExpenseId).Amount)} paid by {Name(g, g.GetExpense(x.ExpenseId).Payer)}",
            ExpenseReversed r => $"#{r.Sequence} {r.ExpenseId} reversed: {r.Reason}",
            PaymentPosted p => $"#{p.Sequence} {p.PaymentId} {Name(g, p.From)} → {Name(g, p.To)} {g.Currency.Format(p.Amount)}",
            PeriodClosed c => $"#{c.Sequence} period '{c.Label}' closed",
            _ => string.Empty,
        }).ToList();
        return items.Count == 0 ? "No posted activity yet." : "Recent activity\n" + string.Join('\n', items);
    }

    public static string Closed(PeriodClosed c) => $"Period '{c.Label}' closed at #{c.ThroughSequence} ({c.PeriodId}). Balances carry forward.";

    public static string Error(DomainException ex) => "⚠ " + ex.Message;

    public static string Invalid(InvalidCommand c) => $"⚠ {c.Problem}\nUsage: {c.Usage}";

    private static string Kind(EntryKind k) => k switch
    {
        EntryKind.Paid => "paid",
        EntryKind.Share => "share",
        EntryKind.PaymentSent => "sent",
        EntryKind.PaymentReceived => "received",
        _ => "reversal",
    };
}
