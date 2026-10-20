namespace Solace.ApiServer.Types.Workshop;

internal sealed record CraftingSlot(
    string? SessionId,
    Guid? RecipeId,
    OutputItem? Output,
    InputItem[]? Escrow,
    int Completed,
    int Available,
    int Total,
    string? NextCompletionUtc,
    string? TotalCompletionUtc,
    State State,
    BoostState? BoostState,
    UnlockPrice? UnlockPrice,
    int StreamVersion
);
