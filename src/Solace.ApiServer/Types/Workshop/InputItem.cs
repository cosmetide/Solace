namespace Solace.ApiServer.Types.Workshop;

internal sealed record InputItem(
     Guid ItemId,
     int Quantity,
     Guid[] InstanceIds
);