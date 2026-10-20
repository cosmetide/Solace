namespace Solace.ApiServer.Types.Inventory;

internal sealed record HotbarItem(
     Guid Id,
     int Count,
     Guid? InstanceId,
     float? Health
);
