using System.Globalization;
using Maijichain.Domain;

namespace Maijichain.Line;

public abstract record ChatCommand;

public sealed record HelpCommand : ChatCommand;

public sealed record JoinCommand(string Nickname) : ChatCommand;

public enum ParticipantKind
{
    Equal,
    Exact,
    Weight,
}

/// <summary>"@amy" (equal), "@amy=600" (exact amount) or "@amy*2" (weight). "all" and "me" are special names.</summary>
public sealed record ParticipantSpec(string Nickname, ParticipantKind Kind, long Value);

public sealed record AddExpenseCommand(long Amount, string Description, IReadOnlyList<ParticipantSpec> Participants, string? PayerNickname, DateOnly? OccurredOn) : ChatCommand;

public sealed record BalanceCommand(string? Nickname) : ChatCommand;

public sealed record PlanCommand : ChatCommand;

public sealed record PaidCommand(long Amount, string ToNickname) : ChatCommand;

public sealed record PaidPlanTransferCommand(string PlanId, int TransferIndex) : ChatCommand;

public sealed record GotCommand(long Amount, string FromNickname) : ChatCommand;

public sealed record ConfirmCommand(string Id) : ChatCommand;

public sealed record CancelCommand(string DraftId) : ChatCommand;

public sealed record RejectCommand(string PaymentId, string Reason) : ChatCommand;

public sealed record UndoCommand(string ExpenseId, string Reason) : ChatCommand;

public sealed record HistoryCommand(int Count) : ChatCommand;

public sealed record CloseCommand(string Label) : ChatCommand;

/// <summary>The message was addressed to the bot but could not be understood; <see cref="Usage"/> explains the syntax.</summary>
public sealed record InvalidCommand(string Problem, string Usage) : ChatCommand;

/// <summary>
/// Parses chat text into commands. Only messages starting with "/" are commands, so ordinary group chatter is ignored.
/// Chinese aliases are accepted for the most common commands.
/// </summary>
public sealed class CommandParser(Currency currency)
{
    public const string AddUsage = "/add <amount> <what> [@nick … | @nick=amount … | @nick*weight …] [by:@payer] [date:yyyy-mm-dd]";

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["add"] = "add", ["記"] = "add", ["記帳"] = "add",
        ["bal"] = "bal", ["balance"] = "bal", ["餘額"] = "bal",
        ["plan"] = "plan", ["settle"] = "plan", ["結算"] = "plan",
        ["paid"] = "paid", ["付"] = "paid",
        ["got"] = "got", ["收"] = "got",
        ["confirm"] = "confirm", ["ok"] = "confirm", ["確認"] = "confirm",
        ["cancel"] = "cancel", ["取消"] = "cancel",
        ["reject"] = "reject",
        ["undo"] = "undo", ["reverse"] = "undo",
        ["history"] = "history", ["log"] = "history",
        ["close"] = "close",
        ["join"] = "join", ["加入"] = "join",
        ["help"] = "help", ["說明"] = "help", ["?"] = "help",
    };

    /// <summary>Returns null when the text is not addressed to the bot.</summary>
    public ChatCommand? ParseText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string trimmed = text.Trim().Replace('／', '/').Replace('＠', '@');
        if (!trimmed.StartsWith('/') || trimmed.Length < 2)
        {
            return null;
        }

        string[] tokens = trimmed[1..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (!Aliases.TryGetValue(tokens[0], out string? verb))
        {
            return new InvalidCommand($"Unknown command /{tokens[0]}.", "Send /help for the command list.");
        }

        string[] args = tokens[1..];
        return verb switch
        {
            "help" => new HelpCommand(),
            "join" => args.Length == 1 ? new JoinCommand(args[0].TrimStart('@')) : new InvalidCommand("Please choose one nickname.", "/join <nickname>"),
            "add" => ParseAdd(args),
            "bal" => args.Length switch
            {
                0 => new BalanceCommand(null),
                1 when args[0].StartsWith('@') => new BalanceCommand(args[0][1..]),
                _ => new InvalidCommand("Unexpected text after /bal.", "/bal or /bal @nick"),
            },
            "plan" => new PlanCommand(),
            "paid" => ParsePaid(args),
            "got" => ParseAmountAndMember(args, "/got <amount> @from", (a, n) => new GotCommand(a, n)),
            "confirm" => args.Length == 1 && IsId(args[0], 'D', 'P') ? new ConfirmCommand(args[0].ToUpperInvariant()) : new InvalidCommand("Which item?", "/confirm D12 or /confirm P3"),
            "cancel" => args.Length == 1 && IsId(args[0], 'D') ? new CancelCommand(args[0].ToUpperInvariant()) : new InvalidCommand("Which draft?", "/cancel D12"),
            "reject" => args.Length >= 1 && IsId(args[0], 'P') ? new RejectCommand(args[0].ToUpperInvariant(), Rest(args, 1, "not received")) : new InvalidCommand("Which payment?", "/reject P3 [reason]"),
            "undo" => args.Length >= 2 && IsId(args[0], 'E') ? new UndoCommand(args[0].ToUpperInvariant(), Rest(args, 1, string.Empty)) : new InvalidCommand("Which expense, and why?", "/undo E7 <reason>"),
            "history" => args.Length == 0 ? new HistoryCommand(10)
                : int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 1 and <= 30 ? new HistoryCommand(n)
                : new InvalidCommand("Count must be 1–30.", "/history [count]"),
            "close" => args.Length >= 1 ? new CloseCommand(Rest(args, 0, string.Empty)) : new InvalidCommand("Give the period a label.", "/close <label>"),
            _ => null,
        };
    }

    /// <summary>Postback data from confirm/cancel buttons, e.g. <c>a=confirm&amp;id=D12</c>.</summary>
    public static ChatCommand? ParsePostback(string? data)
    {
        if (string.IsNullOrEmpty(data))
        {
            return null;
        }

        var fields = data.Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
        string action = fields.GetValueOrDefault("a") ?? string.Empty;
        string id = (fields.GetValueOrDefault("id") ?? string.Empty).ToUpperInvariant();
        return action switch
        {
            "confirm" when IsId(id, 'D', 'P') => new ConfirmCommand(id),
            "cancel" when IsId(id, 'D') => new CancelCommand(id),
            "reject" when IsId(id, 'P') => new RejectCommand(id, "not received"),
            "pay" when TryParsePlanRef(id, out string plan, out int index) => new PaidPlanTransferCommand(plan, index),
            _ => null,
        };
    }

    private ChatCommand ParseAdd(string[] args)
    {
        if (args.Length < 2)
        {
            return new InvalidCommand("An expense needs an amount and a description.", AddUsage);
        }

        if (!currency.TryParse(args[0], out long amount))
        {
            return new InvalidCommand($"'{args[0]}' is not a valid {currency.Code} amount.", AddUsage);
        }

        var participants = new List<ParticipantSpec>();
        var words = new List<string>();
        string? payer = null;
        DateOnly? date = null;
        foreach (string token in args[1..])
        {
            if (token.StartsWith('@'))
            {
                var spec = ParseParticipant(token[1..]);
                if (spec is null)
                {
                    return new InvalidCommand($"Cannot read '{token}'.", AddUsage);
                }

                participants.Add(spec);
            }
            else if (token.StartsWith("by:", StringComparison.OrdinalIgnoreCase))
            {
                payer = token[3..].TrimStart('@');
            }
            else if (token.StartsWith("date:", StringComparison.OrdinalIgnoreCase))
            {
                if (!DateOnly.TryParseExact(token[5..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                {
                    return new InvalidCommand("Dates look like date:2026-11-20.", AddUsage);
                }

                date = d;
            }
            else
            {
                words.Add(token);
            }
        }

        if (words.Count == 0)
        {
            return new InvalidCommand("Please describe the expense.", AddUsage);
        }

        bool anyExact = participants.Any(p => p.Kind == ParticipantKind.Exact);
        if (anyExact && participants.Any(p => p.Kind != ParticipantKind.Exact))
        {
            return new InvalidCommand("When using @nick=amount, give an amount for every participant.", AddUsage);
        }

        if (participants.Any(p => p.Nickname.Equals("all", StringComparison.OrdinalIgnoreCase)) && participants.Count > 1)
        {
            return new InvalidCommand("@all already includes everyone.", AddUsage);
        }

        if (payer is { Length: 0 })
        {
            return new InvalidCommand("by: needs a nickname.", AddUsage);
        }

        return new AddExpenseCommand(amount, string.Join(' ', words), participants, payer, date);
    }

    private ParticipantSpec? ParseParticipant(string body)
    {
        int eq = body.IndexOf('=', StringComparison.Ordinal);
        int star = body.IndexOf('*', StringComparison.Ordinal);
        if (eq > 0)
        {
            return currency.TryParse(body[(eq + 1)..], out long value) && IsNick(body[..eq]) ? new ParticipantSpec(body[..eq], ParticipantKind.Exact, value) : null;
        }

        if (star > 0)
        {
            return int.TryParse(body[(star + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int w) && w is >= 1 and <= 1000 && IsNick(body[..star])
                ? new ParticipantSpec(body[..star], ParticipantKind.Weight, w)
                : null;
        }

        return IsNick(body) ? new ParticipantSpec(body, ParticipantKind.Equal, 1) : null;
    }

    private ChatCommand ParsePaid(string[] args)
    {
        if (args.Length == 1 && TryParsePlanRef(args[0], out string plan, out int index))
        {
            return new PaidPlanTransferCommand(plan, index);
        }

        return ParseAmountAndMember(args, "/paid <amount> @to  or  /paid S2-1", (a, n) => new PaidCommand(a, n));
    }

    private ChatCommand ParseAmountAndMember(string[] args, string usage, Func<long, string, ChatCommand> create)
    {
        if (args.Length != 2)
        {
            return new InvalidCommand("Expected an amount and one @nickname.", usage);
        }

        string amountText = args[0].StartsWith('@') ? args[1] : args[0];
        string nickText = args[0].StartsWith('@') ? args[0] : args[1];
        if (!nickText.StartsWith('@') || !IsNick(nickText[1..]))
        {
            return new InvalidCommand("Expected an @nickname.", usage);
        }

        return currency.TryParse(amountText, out long amount) ? create(amount, nickText[1..]) : new InvalidCommand($"'{amountText}' is not a valid amount.", usage);
    }

    private static bool TryParsePlanRef(string text, out string planId, out int index)
    {
        planId = string.Empty;
        index = -1;
        string[] parts = text.ToUpperInvariant().Split('-');
        if (parts.Length != 2 || !IsId(parts[0], 'S') || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int oneBased) || oneBased < 1)
        {
            return false;
        }

        planId = parts[0];
        index = oneBased - 1;
        return true;
    }

    private static bool IsId(string text, params char[] prefixes) =>
        text.Length is >= 2 and <= 10 && prefixes.Contains(char.ToUpperInvariant(text[0])) && text[1..].All(char.IsAsciiDigit);

    private static bool IsNick(string text) => text.Length is >= 1 and <= 16 && text.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static string Rest(string[] args, int from, string fallback) => args.Length > from ? string.Join(' ', args[from..]) : fallback;
}
