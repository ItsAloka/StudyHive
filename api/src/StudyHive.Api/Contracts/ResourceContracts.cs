namespace StudyHive.Api.Contracts;

/// <summary>
/// The Resource Agent's typed contract (DOCS §11 "The four agents" table). The agent has no
/// database access, so — the same pattern <see cref="PlannerRequest"/> already uses for
/// eligibility — StudyHive.Api reads each consumable's current <see cref="ResourceRequestItem.Available"/>
/// and <see cref="ResourceRequestItem.UnitPrice"/> itself and hands both down on the wire, rather
/// than asking the agent to look them up.
/// </summary>
public sealed class ResourceRequest
{
    public required IReadOnlyList<ResourceRequestItem> RequestedItems { get; init; }
}

public sealed class ResourceRequestItem
{
    public required Guid ConsumableId { get; init; }
    public required string Name { get; init; }
    public required int Requested { get; init; }
    public required int Available { get; init; }
    public required decimal UnitPrice { get; init; }
}

public sealed class ResourceResponseItem
{
    public required Guid ConsumableId { get; init; }
    public required string Name { get; init; }
    public required int Requested { get; init; }
    public required int Available { get; init; }
    public required bool Sufficient { get; init; }
    public required decimal UnitPrice { get; init; }
    public required decimal LineTotal { get; init; }
}

public sealed class ResourceResponse
{
    public required IReadOnlyList<ResourceResponseItem> Items { get; init; }
    public required decimal TotalCost { get; init; }
    public required bool AllAvailable { get; init; }
}
