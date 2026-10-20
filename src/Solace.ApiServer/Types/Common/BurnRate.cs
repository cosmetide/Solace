namespace Solace.ApiServer.Types.Common;

internal sealed record BurnRate(
    int BurnTime,
    int HeatPerSecond
);
