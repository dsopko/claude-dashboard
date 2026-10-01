using ClaudeDashboard.App.Setup;

namespace ClaudeDashboard.Tests.Fakes;

/// <summary>
/// The <c>Run</c> key and its <c>StartupApproved</c> marks, in memory (issue #36). Tests never touch
/// the real ones: the value name <c>Claude Dashboard</c> belongs to the operator's install.
/// </summary>
internal sealed class FakeStartupRegistry : IStartupRegistry
{
    public Dictionary<string, string> Run { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, byte[]> Approval { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every call, in order, as "Verb name".</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Set to make every call throw what a refusing registry throws.</summary>
    public bool Refuses { get; set; }

    public string? ReadRun(string name)
    {
        Record("ReadRun", name);

        return Run.TryGetValue(name, out var data) ? data : null;
    }

    public void WriteRun(string name, string data)
    {
        Record("WriteRun", name);
        Run[name] = data;
    }

    public void DeleteRun(string name)
    {
        Record("DeleteRun", name);
        Run.Remove(name);
    }

    public byte[]? ReadApproval(string name)
    {
        Record("ReadApproval", name);

        return Approval.TryGetValue(name, out var mark) ? mark : null;
    }

    public void DeleteApproval(string name)
    {
        Record("DeleteApproval", name);
        Approval.Remove(name);
    }

    /// <summary>The writes only: what a test means by "the registry was changed".</summary>
    public IEnumerable<string> Writes => Calls.Where(call => !call.StartsWith("Read", StringComparison.Ordinal));

    private void Record(string verb, string name)
    {
        Calls.Add($"{verb} {name}");

        if (Refuses)
        {
            throw new UnauthorizedAccessException("Access to the registry key is denied.");
        }
    }
}
