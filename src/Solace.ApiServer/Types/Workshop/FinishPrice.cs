namespace Solace.ApiServer.Types.Workshop;

internal sealed record FinishPrice(
    int Cost,
    int Discount,
    string ValidTime
);
