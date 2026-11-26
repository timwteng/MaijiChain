using System.Collections.Concurrent;
using Maijichain.Application;
using Maijichain.Domain;

namespace Maijichain.Persistence;

/// <summary>One append-only, checksummed journal file per group: <c>{directory}/{groupId}.journal.jsonl</c>.</summary>
public sealed class FileEventJournal : IEventJournal
{
    private readonly string _directory;
    private readonly Action<string>? _diagnostics;
    private readonly ConcurrentDictionary<string, long> _goodLength = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public FileEventJournal(string directory, Action<string>? diagnostics = null)
    {
        _directory = directory;
        _diagnostics = diagnostics;
        Directory.CreateDirectory(directory);
    }

    public async Task<IReadOnlyList<LedgerEvent>> ReadAllAsync(string groupId, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(groupId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return ReadAndRemember(groupId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task AppendAsync(string groupId, LedgerEvent e, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(groupId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_goodLength.ContainsKey(groupId))
            {
                ReadAndRemember(groupId);
            }

            _goodLength[groupId] = ChecksummedLog.Append(PathFor(groupId), _goodLength[groupId], JournalJson.Serialize(e));
        }
        finally
        {
            gate.Release();
        }
    }

    public string PathFor(string groupId)
    {
        if (groupId.Length is 0 or > 64 || !groupId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new ArgumentException("Group id must be a short file-name-safe identifier.", nameof(groupId));
        }

        return Path.Combine(_directory, $"{groupId}.journal.jsonl");
    }

    private List<LedgerEvent> ReadAndRemember(string groupId)
    {
        string path = PathFor(groupId);
        var result = ChecksummedLog.Read(path);
        if (result.TornTail)
        {
            _diagnostics?.Invoke($"Journal {groupId}: discarded an incomplete final record left by an interrupted write.");
        }

        var events = result.Lines.Select(JournalJson.Deserialize).ToList();
        _goodLength[groupId] = result.GoodLength;
        return events;
    }
}
