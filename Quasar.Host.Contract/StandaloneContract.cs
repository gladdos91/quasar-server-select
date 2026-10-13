using System.Text.RegularExpressions;

namespace Quasar.Host.Contract.V1;

/// <summary>
/// A standalone server belongs to one enrolled Host machine, never to the Quasar web worker.
/// The Host owns its files, runtime and process. Callers must not supply executable paths.
/// </summary>
public sealed record StandaloneServerSpec(
    string ServerId,
    string Name,
    string WorldName,
    int Port,
    bool AutoStart = false)
{
    public static bool ValidId(string? id) =>
        id is not null && Regex.IsMatch(id, "^[a-z][a-z0-9-]{0,47}$");

    public static void Validate(StandaloneServerSpec spec)
    {
        if (!ValidId(spec.ServerId)) throw new ArgumentException("Server ID must be lowercase letters, numbers and hyphens, starting with a letter.");
        if (string.IsNullOrWhiteSpace(spec.Name) || spec.Name.Length > 96)
            throw new ArgumentException("Server name must be 1-96 characters.");
        if (string.IsNullOrWhiteSpace(spec.WorldName) || spec.WorldName.Length > 96 ||
            spec.WorldName is "." or ".." || spec.WorldName.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new ArgumentException("World name must be a simple folder name.");
        if (spec.Port is < 1024 or > 65535)
            throw new ArgumentException("Game UDP port must be between 1024 and 65535.");
    }
}

public sealed record StandaloneServerStatus(
    string ServerId,
    string Name,
    string WorldName,
    int Port,
    bool Running,
    bool Provisioned,
    bool WorldReady,
    bool AutoStart,
    int? ProcessId,
    string Message);

public sealed record StandaloneServerLog(string ServerId, string Text);
