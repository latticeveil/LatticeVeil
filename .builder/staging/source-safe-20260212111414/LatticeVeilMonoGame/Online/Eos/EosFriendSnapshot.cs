namespace LatticeVeilMonoGame.Online.Eos;

public sealed class EosFriendSnapshot
{
    public string AccountId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Status { get; init; } = "";
    public string Presence { get; init; } = "";
    public bool IsHosting { get; init; }
    public string? WorldName { get; init; }
    public string? JoinInfo { get; init; }
}
