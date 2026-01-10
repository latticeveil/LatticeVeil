namespace RedactedCraftMonoGame.Core;

public sealed class GameStartOptions
{
    public string? JoinToken { get; init; }
    public bool Offline { get; init; }

    public bool HasJoinToken => !string.IsNullOrWhiteSpace(JoinToken);
}

