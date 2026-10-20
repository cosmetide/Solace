using System.Diagnostics;
using System.Text;
using Solace.Buildplate.Connector.Model;
using Solace.Common;
using Solace.Common.Utils;
using Solace.Db.Earth;
using Solace.Db.Earth.Models.Player;
using Solace.EventBus.Client;
using Solace.ObjectStore.Client;
using Solace.StaticData;
using CICIBIEType = Solace.StaticData.Catalog.ItemsCatalogR.Item.BoostEffectType;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace Solace.ApiServer.Utils;

internal sealed partial class BuildplateInstanceRequestHandler : IAsyncDisposable
{
    private readonly IDbContextFactory<EarthDbContext> _earthDbFactory;
    private readonly ObjectStoreClient _objectStoreClient;
    private readonly Catalog _catalog;
    private readonly BuildplateInstancesManager _buildplateInstancesManager;

    private readonly ILogger<BuildplateInstanceRequestHandler> _logger;

    private RequestHandler? _requestHandler;

    public BuildplateInstanceRequestHandler(IDbContextFactory<EarthDbContext> earthDbFactory, ObjectStoreClient objectStoreClient, StaticData.StaticDataProvider staticData, BuildplateInstancesManager buildplateInstancesManager, ILogger<BuildplateInstanceRequestHandler> logger)
    {
        _earthDbFactory = earthDbFactory;
        _objectStoreClient = objectStoreClient;
        _catalog = staticData.Catalog;
        _buildplateInstancesManager = buildplateInstancesManager;
        _logger = logger;
    }

    internal async Task InitializeAsync(EventBusClient eventBusClient)
        => _requestHandler = await eventBusClient.AddRequestHandlerAsync("buildplates",
            async (request, cancellationToken) =>
            {
                try
                {
                    var requestData = (string)request.Data.Value!;
                    LogHandlingBuildplatesRequest(request.Type);

                    switch (request.Type)
                    {
                        case "load":
                            {
                                var buildplateLoadRequest = ReadRawRequest<BuildplateLoadRequest>(requestData, _logger);
                                if (buildplateLoadRequest is null)
                                {
                                    return null;
                                }

                                var buildplateLoadResponse = await HandleLoadAsync(buildplateLoadRequest.PlayerId, buildplateLoadRequest.BuildplateId, cancellationToken);
                                return buildplateLoadResponse is not null ? Json.Serialize(buildplateLoadResponse) : (MessagePayload?)null;
                            }
                        case "loadShared":
                            {
                                var sharedBuildplateLoadRequest = ReadRawRequest<SharedBuildplateLoadRequest>(requestData, _logger);
                                if (sharedBuildplateLoadRequest is null)
                                {
                                    return null;
                                }

                                var buildplateLoadResponse = await HandleLoadSharedAsync(sharedBuildplateLoadRequest.SharedBuildplateId, cancellationToken);
                                return buildplateLoadResponse is not null ? Json.Serialize(buildplateLoadResponse) : (MessagePayload?)null;
                            }
                        case "loadEncounter":

                            {
                                var encounterBuildplateLoadRequest = ReadRawRequest<EncounterBuildplateLoadRequest>(requestData, _logger);
                                if (encounterBuildplateLoadRequest is null)
                                {
                                    return null;
                                }

                                var buildplateLoadResponse = await HandleLoadEncounterAsync(encounterBuildplateLoadRequest.EncounterBuildplateId, cancellationToken);
                                return buildplateLoadResponse is not null ? Json.Serialize(buildplateLoadResponse) : (MessagePayload?)null;
                            }
                        case "saved":
                            {
                                var requestWithInstanceId = ReadRequest<WorldSavedMessage>(requestData);
                                return requestWithInstanceId is null
                                    ? null
                                    : await HandleSavedAsync(requestWithInstanceId.InstanceId, requestWithInstanceId.Request.DataBase64, request.Timestamp, cancellationToken) ? "" : (MessagePayload?)null;
                            }
                        case "playerConnected":
                            {
                                LogHandlingPlayerConnectedRawPayload(requestData);
                                Guid instanceId;
                                PlayerConnectedRequest? playerConnectedRequest;
                                var requestWithInstanceId = ReadRequest<PlayerConnectedRequest>(requestData);
                                if (requestWithInstanceId is not null)
                                {
                                    instanceId = requestWithInstanceId.InstanceId;
                                    playerConnectedRequest = requestWithInstanceId.Request;
                                    LogPlayerConnectedExplicitInstanceId(instanceId);
                                }
                                else
                                {
                                    playerConnectedRequest = ReadRawRequest<PlayerConnectedRequest>(requestData, _logger);
                                    if (playerConnectedRequest is null)
                                    {
                                        LogFailedToParsePlayerConnectedRequest(requestData);
                                        return null;
                                    }

                                    var resolvedInstanceId = _buildplateInstancesManager.GetInstanceIdForPlayer(playerConnectedRequest.Uuid);
                                    if (resolvedInstanceId is null)
                                    {
                                        LogCouldNotResolveInstanceIdForPlayer(playerConnectedRequest.Uuid);
                                        return null;
                                    }

                                    instanceId = resolvedInstanceId.Value;
                                    LogResolvedInstanceIdForPlayer(instanceId, playerConnectedRequest.Uuid);
                                }

                                var playerConnectedResponse = await HandlePlayerConnectedAsync(instanceId, playerConnectedRequest, cancellationToken);
                                if (playerConnectedResponse is null)
                                {
                                    LogHandlePlayerConnectedReturnedNull(instanceId, playerConnectedRequest?.Uuid);
                                    return null;
                                }

                                return Json.Serialize(playerConnectedResponse);
                            }
                        case "playerDisconnected":
                            {
                                Guid instanceId;
                                PlayerDisconnectedRequest? playerDisconnectedRequest;
                                var requestWithInstanceId = ReadRequest<PlayerDisconnectedRequest>(requestData);
                                if (requestWithInstanceId is not null)
                                {
                                    instanceId = requestWithInstanceId.InstanceId;
                                    playerDisconnectedRequest = requestWithInstanceId.Request;
                                }
                                else
                                {
                                    playerDisconnectedRequest = ReadRawRequest<PlayerDisconnectedRequest>(requestData, _logger);
                                    if (playerDisconnectedRequest is null)
                                    {
                                        return null;
                                    }

                                    var resolved = _buildplateInstancesManager.GetInstanceIdForPlayer(playerDisconnectedRequest.PlayerId);
                                    if (resolved is null)
                                    {
                                        return null;
                                    }

                                    instanceId = resolved.Value;
                                }

                                var playerDisconnectedResponse = await HandlePlayerDisconnectedAsync(instanceId, playerDisconnectedRequest, request.Timestamp, cancellationToken);
                                return playerDisconnectedResponse is not null ? Json.Serialize(playerDisconnectedResponse) : (MessagePayload?)null;
                            }
                        case "playerDead":
                            {
                                Guid instanceId;
                                Guid playerId;
                                var requestWithInstanceId = ReadRequest<Guid>(requestData);
                                if (requestWithInstanceId is not null)
                                {
                                    instanceId = requestWithInstanceId.InstanceId;
                                    playerId = requestWithInstanceId.Request;
                                }
                                else
                                {
                                    var rawPlayerId = ReadRawRequest<string>(requestData, _logger);
                                    if (rawPlayerId is null || !Guid.TryParse(rawPlayerId, out playerId))
                                    {
                                        return null;
                                    }

                                    var resolved = _buildplateInstancesManager.GetInstanceIdForPlayer(playerId);
                                    if (resolved is null)
                                    {
                                        return null;
                                    }

                                    instanceId = resolved.Value;
                                }

                                var respawn = HandlePlayerDead(instanceId, playerId, request.Timestamp);
                                return respawn is not null ? Json.Serialize(respawn.Value) : (MessagePayload?)null;
                            }
                        case "getInitialPlayerState":
                            {
                                Guid instanceId;
                                Guid playerId;
                                var requestWithInstanceId = ReadRequest<Guid>(requestData);
                                if (requestWithInstanceId is not null)
                                {
                                    instanceId = requestWithInstanceId.InstanceId;
                                    playerId = requestWithInstanceId.Request;
                                }
                                else
                                {
                                    var rawPlayerId = ReadRawRequest<string>(requestData, _logger);
                                    if (rawPlayerId is null || !Guid.TryParse(rawPlayerId, out playerId))
                                    {
                                        return null;
                                    }

                                    var resolved = _buildplateInstancesManager.GetInstanceIdForPlayer(playerId);
                                    if (resolved is null)
                                    {
                                        return null;
                                    }

                                    instanceId = resolved.Value;
                                }

                                var initialPlayerStateResponse = await HandleGetInitialPlayerStateAsync(instanceId, playerId, request.Timestamp, cancellationToken);
                                return initialPlayerStateResponse is not null ? Json.Serialize(initialPlayerStateResponse) : (MessagePayload?)null;
                            }
                        case "getInventory":
                            {
                                Guid instanceId;
                                Guid playerId;
                                var requestWithInstanceId = ReadRequest<Guid>(requestData);
                                if (requestWithInstanceId is not null)
                                {
                                    instanceId = requestWithInstanceId.InstanceId;
                                    playerId = requestWithInstanceId.Request;
                                }
                                else
                                {
                                    var rawPlayerId = ReadRawRequest<string>(requestData, _logger);
                                    if (rawPlayerId is null || !Guid.TryParse(rawPlayerId, out playerId))
                                    {
                                        return null;
                                    }

                                    var resolved = _buildplateInstancesManager.GetInstanceIdForPlayer(playerId);
                                    if (resolved is null)
                                    {
                                        return null;
                                    }

                                    instanceId = resolved.Value;
                                }

                                var inventoryResponse = await HandleGetInventoryAsync(instanceId, playerId, cancellationToken);
                                return inventoryResponse is not null ? Json.Serialize(inventoryResponse) : (MessagePayload?)null;
                            }
                        case "inventoryAdd":
                            {
                                Guid instanceId;
                                InventoryAddItemMessage? inventoryAddItemMessage;
                                var requestWithInstanceId = ReadRequest<InventoryAddItemMessage>(requestData);
                                if (requestWithInstanceId is not null)
                                {
                                    instanceId = requestWithInstanceId.InstanceId;
                                    inventoryAddItemMessage = requestWithInstanceId.Request;
                                }
                                else
                                {
                                    inventoryAddItemMessage = ReadRawRequest<InventoryAddItemMessage>(requestData, _logger);
                                    if (inventoryAddItemMessage is null)
                                    {
                                        return null;
                                    }

                                    var resolved = _buildplateInstancesManager.GetInstanceIdForPlayer(inventoryAddItemMessage.PlayerId);
                                    if (resolved is null)
                                    {
                                        return null;
                                    }

                                    instanceId = resolved.Value;
                                }

                                return await HandleInventoryAddAsync(instanceId, inventoryAddItemMessage, request.Timestamp, cancellationToken) ? "" : (MessagePayload?)null;
                            }
                        case "inventoryRemove":
                            {
                                Guid instanceId;
                                InventoryRemoveItemRequest? inventoryRemoveItemRequest;
                                var requestWithBuildplateId = ReadRequest<InventoryRemoveItemRequest>(requestData);
                                if (requestWithBuildplateId is not null)
                                {
                                    instanceId = requestWithBuildplateId.InstanceId;
                                    inventoryRemoveItemRequest = requestWithBuildplateId.Request;
                                }
                                else
                                {
                                    inventoryRemoveItemRequest = ReadRawRequest<InventoryRemoveItemRequest>(requestData, _logger);
                                    if (inventoryRemoveItemRequest is null)
                                    {
                                        return null;
                                    }

                                    var resolved = _buildplateInstancesManager.GetInstanceIdForPlayer(inventoryRemoveItemRequest.PlayerId);
                                    if (resolved is null)
                                    {
                                        return null;
                                    }

                                    instanceId = resolved.Value;
                                }

                                var response = await HandleInventoryRemoveAsync(instanceId, inventoryRemoveItemRequest, cancellationToken);
                                return response is not null ? Json.Serialize(response) : (MessagePayload?)null;
                            }
                        case "inventoryUpdateWear":
                            {
                                Guid instanceId;
                                InventoryUpdateItemWearMessage? inventoryUpdateItemWearMessage;
                                var requestWithInstanceId = ReadRequest<InventoryUpdateItemWearMessage>(requestData);
                                if (requestWithInstanceId is not null)
                                {
                                    instanceId = requestWithInstanceId.InstanceId;
                                    inventoryUpdateItemWearMessage = requestWithInstanceId.Request;
                                }
                                else
                                {
                                    inventoryUpdateItemWearMessage = ReadRawRequest<InventoryUpdateItemWearMessage>(requestData, _logger);
                                    if (inventoryUpdateItemWearMessage is null)
                                    {
                                        return null;
                                    }

                                    var resolved = _buildplateInstancesManager.GetInstanceIdForPlayer(inventoryUpdateItemWearMessage.PlayerId);
                                    if (resolved is null)
                                    {
                                        return null;
                                    }

                                    instanceId = resolved.Value;
                                }

                                return await HandleInventoryUpdateWearAsync(instanceId, inventoryUpdateItemWearMessage, cancellationToken) ? "" : (MessagePayload?)null;
                            }
                        case "inventorySetHotbar":
                            {
                                Guid instanceId;
                                InventorySetHotbarMessage? inventorySetHotbarMessage;
                                var requestWithInstanceId = ReadRequest<InventorySetHotbarMessage>(requestData);
                                if (requestWithInstanceId is not null)
                                {
                                    instanceId = requestWithInstanceId.InstanceId;
                                    inventorySetHotbarMessage = requestWithInstanceId.Request;
                                }
                                else
                                {
                                    inventorySetHotbarMessage = ReadRawRequest<InventorySetHotbarMessage>(requestData, _logger);
                                    if (inventorySetHotbarMessage is null)
                                    {
                                        return null;
                                    }

                                    var resolved = _buildplateInstancesManager.GetInstanceIdForPlayer(inventorySetHotbarMessage.PlayerId);
                                    if (resolved is null)
                                    {
                                        return null;
                                    }

                                    instanceId = resolved.Value;
                                }

                                return await HandleInventorySetHotbarAsync(instanceId, inventorySetHotbarMessage, cancellationToken) ? "" : (MessagePayload?)null;
                            }
                        case "findPlayerInstance":
                            {
                                var findPlayerRequest = ReadRawRequest<FindPlayerIdRequest>(requestData, _logger);
                                if (findPlayerRequest is null || !Guid.TryParse(findPlayerRequest.MinecraftName, out var playerId))
                                {
                                    return null;
                                }

                                var instanceInfo = _buildplateInstancesManager.GetInstanceInfoForPlayer(playerId);
                                if (instanceInfo is null)
                                {
                                    return null;
                                }

                                var inventoryType = instanceInfo.Type switch
                                {
                                    BuildplateInstancesManager.InstanceType.BUILD => InventoryType.SYNCED,
                                    BuildplateInstancesManager.InstanceType.ENCOUNTER => InventoryType.BACKPACK,
                                    _ => InventoryType.DISCARD,
                                };
                                var survival = instanceInfo.Type is not BuildplateInstancesManager.InstanceType.BUILD and not BuildplateInstancesManager.InstanceType.SHARED_BUILD;

                                var response = new PlayerInstanceInfoResponse(
                                    instanceInfo.InstanceId,
                                    inventoryType,
                                    survival,
                                    false
                                );

                                return Json.Serialize(response);
                            }
                        default:
                            return null;
                    }
                }
                catch (Exception exception) when (exception is DbUpdateException or DbUpdateConcurrencyException)
                {
                    LogDatabaseErrorWhileHandlingRequest(exception);
                    return null;
                }
            },
            async exception =>
            {
                LogBuildplatesEventBusRequestHandlerError(exception);
                Console.Error.WriteLine(exception);
                Console.Error.Flush();
                Environment.Exit(1);
            }
        );

    public async ValueTask DisposeAsync()
    {
        if (_requestHandler is not null)
        {
            await _requestHandler.DisposeAsync();
        }
    }

    private sealed record BuildplateLoadRequest(
        Guid PlayerId,
        Guid BuildplateId
    );

    private sealed record SharedBuildplateLoadRequest(
        Guid SharedBuildplateId
    );

    private sealed record EncounterBuildplateLoadRequest(
        Guid EncounterBuildplateId
    );

    private sealed record BuildplateLoadResponse(
        string ServerDataBase64
    );

    private async Task<BuildplateLoadResponse?> HandleLoadAsync(Guid accountId, Guid buildplateId, CancellationToken cancellationToken = default)
    {
        await using var earthDb = await _earthDbFactory.CreateDbContextAsync(cancellationToken);

        var buildplate = await earthDb.PlayerBuildplates
            .AsNoTracking()
            .Where(buildplate => buildplate.Id == buildplateId && buildplate.ProfileId == accountId)
            .Select(buildplate => new { buildplate.ServerDataObjectId, })
            .FirstOrDefaultAsync(cancellationToken: cancellationToken);

        if (buildplate is null)
        {
            return null;
        }

        var serverData = await _objectStoreClient.GetMemoryAsync(buildplate.ServerDataObjectId, cancellationToken);
        if (serverData is null)
        {
            LogFailedToGetServerData(buildplate.ServerDataObjectId, buildplateId);
            return null;
        }

        var serverDataBase64 = Convert.ToBase64String(serverData.Value.Span);

        return new BuildplateLoadResponse(serverDataBase64);
    }

    private async Task<BuildplateLoadResponse?> HandleLoadSharedAsync(Guid sharedBuildplateId, CancellationToken cancellationToken = default)
    {
        await using var earthDb = await _earthDbFactory.CreateDbContextAsync(cancellationToken);

        var sharedBuildplate = await earthDb.SharedBuildplates
            .AsNoTracking()
            .Where(sharedBuildplate => sharedBuildplate.Id == sharedBuildplateId)
            .Select(sharedBuildplate => new { sharedBuildplate.ServerDataObjectId, })
            .FirstOrDefaultAsync(cancellationToken: cancellationToken);

        if (sharedBuildplate is null)
        {
            return null;
        }

        var serverData = await _objectStoreClient.GetMemoryAsync(sharedBuildplate.ServerDataObjectId, cancellationToken);
        if (serverData is null)
        {
            LogFailedToGetServerDataShared(sharedBuildplate.ServerDataObjectId, sharedBuildplateId);
            return null;
        }

        var serverDataBase64 = Convert.ToBase64String(serverData.Value.Span);

        return new BuildplateLoadResponse(serverDataBase64);
    }

    private async Task<BuildplateLoadResponse?> HandleLoadEncounterAsync(Guid encounterBuildplateId, CancellationToken cancellationToken = default)
    {
        await using var earthDb = await _earthDbFactory.CreateDbContextAsync(cancellationToken);

        var encounterBuildplate = await earthDb.EncounterBuildplates
            .AsNoTracking()
            .Where(encounterBuildplate => encounterBuildplate.Id == encounterBuildplateId)
            .Select(encounterBuildplate => new { encounterBuildplate.ServerDataObjectId, })
            .FirstOrDefaultAsync(cancellationToken);

        if (encounterBuildplate is null)
        {
            return null;
        }

        var serverData = await _objectStoreClient.GetMemoryAsync(encounterBuildplate.ServerDataObjectId, cancellationToken);
        if (serverData is null)
        {
            LogFailedToGetServerDataEncounter(encounterBuildplate.ServerDataObjectId, encounterBuildplateId);
            return null;
        }

        var serverDataBase64 = Convert.ToBase64String(serverData.Value.Span);

        return new BuildplateLoadResponse(serverDataBase64);
    }

    private async Task<bool> HandleSavedAsync(Guid instanceId, string dataBase64, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
    {
        var instanceInfo = _buildplateInstancesManager.GetInstanceInfo(instanceId);
        if (instanceInfo is null)
        {
            return false;
        }

        if (instanceInfo.Type != BuildplateInstancesManager.InstanceType.BUILD)
        {
            return false;
        }

        var accountId = instanceInfo.PlayerId;
        var buildplateId = instanceInfo.BuildplateId;

        Debug.Assert(accountId is not null);

        byte[] serverData;
        try
        {
            serverData = Convert.FromBase64String(dataBase64);
        }
        catch
        {
            return false;
        }

        await using var earthDb = await _earthDbFactory.CreateDbContextAsync(cancellationToken);

        var buildplateUnsafeForPreviewGenerator = await earthDb.PlayerBuildplates
            .AsNoTracking()
            .Where(buildplate => buildplate.Id == buildplateId)
            .Select(buildplate => new { buildplate.Night, })
            .FirstOrDefaultAsync(cancellationToken);

        if (buildplateUnsafeForPreviewGenerator is null)
        {
            return false;
        }

        var preview = await _buildplateInstancesManager.GetBuildplatePreviewAsync(serverData, buildplateUnsafeForPreviewGenerator.Night, cancellationToken);
        if (preview is null)
        {
            LogCouldNotGeneratePreviewForBuildplate();
        }

        var serverDataObjectId = await _objectStoreClient.StoreAsync(serverData, cancellationToken);
        if (serverDataObjectId is null)
        {
            LogFailedToStoreData(buildplateId);
            return false;
        }

        Guid? previewObjectId;
        if (preview is not null)
        {
            previewObjectId = await _objectStoreClient.StoreAsync(Encoding.ASCII.GetBytes(preview), cancellationToken);
            if (previewObjectId is null)
            {
                LogFailedToStorePreview(buildplateId);
            }
        }
        else
        {
            previewObjectId = null;
        }

        try
        {
            var buildplate = await earthDb.PlayerBuildplates
                .AsTracking()
                .FirstOrDefaultAsync(buildplate => buildplate.Id == buildplateId && buildplate.ProfileId == accountId, cancellationToken);

            if (buildplate is null)
            {
                await _objectStoreClient.DeleteAsync(serverDataObjectId.Value, cancellationToken);
                if (previewObjectId is not null)
                {
                    await _objectStoreClient.DeleteAsync(previewObjectId.Value, cancellationToken);
                }

                return false;
            }

            var oldServerDataObjectId = buildplate.ServerDataObjectId;

            buildplate.LastModified = timestamp;
            buildplate.ServerDataObjectId = serverDataObjectId.Value;

            Guid? oldPreviewObjectId;
            if (previewObjectId is not null)
            {
                oldPreviewObjectId = buildplate.PreviewObjectId;
                buildplate.PreviewObjectId = previewObjectId.Value;
            }
            else
            {
                oldPreviewObjectId = null;
            }

            await earthDb.SaveChangesAsync(cancellationToken);

            await _objectStoreClient.DeleteAsync(oldServerDataObjectId, cancellationToken);

            if (!Guid.IsNullOrZero(oldPreviewObjectId))
            {
                await _objectStoreClient.DeleteAsync(oldPreviewObjectId.Value, cancellationToken);
            }

            LogStoredNewSnapshotForBuildplate(buildplateId);

            return true;
        }
        catch (Exception exception) when (exception is DbUpdateException or DbUpdateConcurrencyException)
        {
            LogErrorSavingWorld(exception);

            await _objectStoreClient.DeleteAsync(serverDataObjectId.Value, cancellationToken);
            if (previewObjectId is not null)
            {
                await _objectStoreClient.DeleteAsync(previewObjectId.Value, cancellationToken);
            }

            throw;
        }
    }

    private async Task<PlayerConnectedResponse?> HandlePlayerConnectedAsync(Guid instanceId, PlayerConnectedRequest playerConnectedRequest, CancellationToken cancellationToken = default)
    {
        // TODO: check join code etc.

        var instanceInfo = _buildplateInstancesManager.GetInstanceInfo(instanceId);

        if (instanceInfo is null)
        {
            LogPlayerConnectedGetInstanceInfoReturnedNull(instanceId);
            return null;
        }

        await using var earthDb = await _earthDbFactory.CreateDbContextAsync(cancellationToken);

        InventoryResponse? initialInventoryContents;
        switch (instanceInfo.Type)
        {
            case BuildplateInstancesManager.InstanceType.BUILD:
                {
                    initialInventoryContents = null;
                }

                break;
            case BuildplateInstancesManager.InstanceType.PLAY:
                {
                    var stackableItems = earthDb.StackableItems
                        .AsNoTracking()
                        .Where(item => item.ProfileId == playerConnectedRequest.Uuid)
                        .AsAsyncEnumerable();

                    var nonStackableItems = earthDb.NonStackableItems
                        .AsNoTracking()
                        .Where(item => item.ProfileId == playerConnectedRequest.Uuid)
                        .AsAsyncEnumerable();

                    var hotbar = await earthDb.Hotbars
                        .AsNoTracking()
                        .FirstAsync(hotbar => hotbar.Id == playerConnectedRequest.Uuid, cancellationToken: cancellationToken);

                    initialInventoryContents = new InventoryResponse(
                        await AsyncEnumerable.Concat(
                            stackableItems
                                .Select(item => new InventoryResponseItem(item.ItemId, item.Count, null, 0)),
                            nonStackableItems
                                .Select(instance => new InventoryResponseItem(instance.ItemId, 1, instance.InstanceId, instance.Wear))
                        ).Where(item => item.Count > 0).ToArrayAsync(cancellationToken),
                        [.. hotbar.Items.Select(item => item is { Count: > 0 } ? new InventoryResponseHotbarItem(item.Uuid, item.Count, item.InstanceId) : null)]
                    );
                }

                break;
            case BuildplateInstancesManager.InstanceType.SHARED_BUILD or BuildplateInstancesManager.InstanceType.SHARED_PLAY:
                {
                    var sharedBuildplate = await earthDb.SharedBuildplates
                        .AsNoTracking()
                        .FirstOrDefaultAsync(sharedBuildplate => sharedBuildplate.Id == instanceInfo.BuildplateId, cancellationToken: cancellationToken);

                    if (sharedBuildplate is null)
                    {
                        return null;
                    }

                    initialInventoryContents = new InventoryResponse(
                        [.. Enumerable.Concat(
                            sharedBuildplate.Hotbar
                                .Where(item => item is { Count: > 0, InstanceId: null })
                                .GroupBy(item => item!.Uuid)
                                .ToDictionary(
                                    group => group.Key,
                                    group => group.Sum(item => item!.Count)
                                )
                                .Select(entry => new InventoryResponseItem(entry.Key, entry.Value, null, 0)),
                            sharedBuildplate.Hotbar
                                .Where(item => item is { Count: > 0, InstanceId: not null })
                                .Select(item => new InventoryResponseItem(item!.Uuid, 1, item.InstanceId, item.Wear))
                        )],
                        [.. sharedBuildplate.Hotbar.Select(item => item is { Count: > 0 } ? new InventoryResponseHotbarItem(item.Uuid, item.Count, item.InstanceId) : null)]
                    );
                }

                break;
            case BuildplateInstancesManager.InstanceType.ENCOUNTER:
                {
                    var hotbar = await earthDb.Hotbars
                        .AsTracking()
                        .FirstAsync(hotbar => hotbar.Id == playerConnectedRequest.Uuid, cancellationToken: cancellationToken);

                    var inventoryResponseHotbar = new InventoryResponseHotbarItem[7];
                    Dictionary<Guid, int> inventoryResponseStackableItems = [];
                    LinkedList<InventoryResponseItem> inventoryResponseNonStackableItems = [];
                    for (var index = 0; index < 7; index++)
                    {
                        var item = hotbar.Items[index];
                        if (item is not null)
                        {
                            if (item.InstanceId is null)
                            {
                                await InventoryUtils.TakeStackableItemsAsync(earthDb, ResultsEF.Builder.Null, playerConnectedRequest.Uuid, item.Uuid, item.Count, cancellationToken);
                                inventoryResponseStackableItems[item.Uuid] = inventoryResponseStackableItems.GetValueOrDefault(item.Uuid, 0) + item.Count;
                                inventoryResponseHotbar[index] = new InventoryResponseHotbarItem(item.Uuid, item.Count, null);
                            }
                            else
                            {
                                var wear = (await InventoryUtils.TakeInstanceItemsAsync(earthDb, ResultsEF.Builder.Null, playerConnectedRequest.Uuid, item.Uuid, [item.InstanceId.Value], cancellationToken)).First().Wear;
                                inventoryResponseNonStackableItems.AddLast(new InventoryResponseItem(item.Uuid, 1, item.InstanceId, wear));
                                inventoryResponseHotbar[index] = new InventoryResponseHotbarItem(item.Uuid, 1, item.InstanceId);
                            }
                        }
                    }

                    await HotbarUtils.LimitToInventoryAsync(earthDb, playerConnectedRequest.Uuid, hotbar, cancellationToken);

                    initialInventoryContents = new InventoryResponse(
                        [
                            .. inventoryResponseStackableItems.Select(entry => new InventoryResponseItem(entry.Key, entry.Value, null, 0)),
                            .. inventoryResponseNonStackableItems
                        ],
                        inventoryResponseHotbar
                    );

                    await earthDb.SaveChangesAsync(cancellationToken);
                }

                break;
            default:
                {
                    // shouldn't happen, safe default
                    LogExpectedBackpackContentsInPlayerDisconnectedRequest(instanceInfo.Type);
                    initialInventoryContents = new InventoryResponse([], new InventoryResponseHotbarItem[7]);
                }

                break;
        }

        var inventoryType = instanceInfo.Type switch
        {
            BuildplateInstancesManager.InstanceType.BUILD => InventoryType.SYNCED,
            BuildplateInstancesManager.InstanceType.PLAY => InventoryType.DISCARD,
            BuildplateInstancesManager.InstanceType.SHARED_BUILD => InventoryType.DISCARD,
            BuildplateInstancesManager.InstanceType.SHARED_PLAY => InventoryType.DISCARD,
            BuildplateInstancesManager.InstanceType.ENCOUNTER => InventoryType.BACKPACK,
            _ => InventoryType.DISCARD,
        };

        var playerConnectedResponse = new PlayerConnectedResponse(
            true,
            initialInventoryContents,
            inventoryType,
            instanceId
        );

        return playerConnectedResponse;
    }

    private async Task<PlayerDisconnectedResponse?> HandlePlayerDisconnectedAsync(Guid instanceId, PlayerDisconnectedRequest playerDisconnectedRequest, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
    {
        var instanceInfo = _buildplateInstancesManager.GetInstanceInfo(instanceId);
        if (instanceInfo is null)
        {
            return null;
        }

        var usesBackpack = instanceInfo.Type is BuildplateInstancesManager.InstanceType.ENCOUNTER;
        if (usesBackpack)
        {
            await using var earthDb = await _earthDbFactory.CreateDbContextAsync(cancellationToken);

            var backpackContents = playerDisconnectedRequest.BackpackContents;
            if (backpackContents is null)
            {
                LogExpectedBackpackContentsInPlayerDisconnectedRequest();
                return null;
            }

            var hotbar = await earthDb.Hotbars
                .AsTracking()
                .FirstAsync(hotbar => hotbar.Id == playerDisconnectedRequest.PlayerId, cancellationToken: cancellationToken);

            LinkedList<Guid> unlockedJournalItems = [];
            foreach (var item in backpackContents.Items)
            {
                var catalogItem = _catalog.ItemsCatalog.GetItem(item.Id);
                if (catalogItem is null)
                {
                    LogBackpackContentsContainedItemThatIsNotInItemCatalog();
                    continue;
                }

                if (!catalogItem.Stackable && item.InstanceId is null)
                {
                    LogBackpackContentsContainedNonStackableItemWithoutInstanceId();
                    continue;
                }

                if (catalogItem.Stackable)
                {
                    await InventoryUtils.AddStackableItemsAsync(earthDb, ResultsEF.Builder.Null, playerDisconnectedRequest.PlayerId, item.Id, item.Count, cancellationToken);
                }
                else
                {
                    Debug.Assert(item.InstanceId is not null);

                    await InventoryUtils.AddInstanceItemsAsync(earthDb, ResultsEF.Builder.Null, [new NonStackableItemInstanceEF(playerDisconnectedRequest.PlayerId, item.Id, item.InstanceId.Value, item.Wear)], cancellationToken);
                }

                if (await JournalUtils.AddCollectedItemAsync(earthDb, ResultsEF.Builder.Null, playerDisconnectedRequest.PlayerId, item.Id, timestamp, item.Count, cancellationToken) is 0)
                {
                    if (catalogItem.JournalEntry is not null)
                    {
                        unlockedJournalItems.AddLast(item.Id);
                    }
                }
            }

            for (var index = 0; index < 7; index++)
            {
                var hotbarItem = backpackContents.Hotbar[index];
                if (hotbarItem is not null)
                {
                    hotbar.Items[index] = new HotbarEF.Item(hotbarItem.Id, hotbarItem.Count, hotbarItem.InstanceId);
                }
            }

            await HotbarUtils.LimitToInventoryAsync(earthDb, playerDisconnectedRequest.PlayerId, hotbar, cancellationToken);

            await earthDb.SaveChangesAsync(cancellationToken);

            foreach (var itemId in unlockedJournalItems)
            {
                await TokenUtils.AddTokenAsync(earthDb, ResultsEF.Builder.Null, new JournalItemUnlockedTokenEF(playerDisconnectedRequest.PlayerId, itemId), cancellationToken);
            }
        }

        if (instanceInfo.PlayerId is not null && instanceInfo.PlayerId == playerDisconnectedRequest.PlayerId)
        {
            LogHostPlayerDisconnectedStoppingInstance(playerDisconnectedRequest.PlayerId, instanceId);
            _ = Task.Run(async () =>
            {
                try
                {
                    await _buildplateInstancesManager.StopInstanceAsync(instanceId, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    LogFailedToStopInstanceAfterHostDisconnect(exception, instanceId);
                }
            }, CancellationToken.None);
        }

        return new PlayerDisconnectedResponse();
    }

    private bool? HandlePlayerDead(Guid instanceId, Guid playerId, DateTimeOffset currentTime)
    {
        _ = playerId;
        _ = currentTime;

        var instanceInfo = _buildplateInstancesManager.GetInstanceInfo(instanceId);
        return instanceInfo is null
            ? null
            : instanceInfo.Type is BuildplateInstancesManager.InstanceType.BUILD or BuildplateInstancesManager.InstanceType.SHARED_BUILD;
    }

    private sealed record EffectInfo(
        DateTimeOffset EndTime,
        Catalog.ItemsCatalogR.Item.BoostEffect Effect
    );

    private async Task<InitialPlayerStateResponse?> HandleGetInitialPlayerStateAsync(Guid instanceId, Guid accountId, DateTimeOffset currentTime, CancellationToken cancellationToken = default)
    {
        var instanceInfo = _buildplateInstancesManager.GetInstanceInfo(instanceId);

        if (instanceInfo is null)
        {
            return null;
        }

        var (useHealth, useBoosts) = instanceInfo.Type switch
        {
            BuildplateInstancesManager.InstanceType.BUILD => (false, false),
            BuildplateInstancesManager.InstanceType.PLAY => (false, true),
            BuildplateInstancesManager.InstanceType.SHARED_BUILD => (false, false),
            BuildplateInstancesManager.InstanceType.SHARED_PLAY => (false, true),
            BuildplateInstancesManager.InstanceType.ENCOUNTER => (true, true),
            _ => (false, false),
        };

        if (!useHealth && !useBoosts)
        {
            return new InitialPlayerStateResponse(20.0f, []);
        }
        else
        {
            if (!useBoosts)
            {
                throw new UnreachableException();
            }

            await using var earthDb = await _earthDbFactory.CreateDbContextAsync(cancellationToken);

            var profileHealth = await earthDb.Profiles
                .AsNoTracking()
                .Where(profile => profile.Id == accountId)
                .Select(profile => profile.Health)
                .FirstAsync(cancellationToken: cancellationToken);

            var boosts = await earthDb.Boosts
                .AsNoTracking()
                .FirstAsync(boosts => boosts.Id == accountId, cancellationToken: cancellationToken);

            var maxHealth = (float)Common.Utils.BoostUtils.GetMaxPlayerHealth(boosts, currentTime, _catalog);

            return new InitialPlayerStateResponse(
                useHealth ? float.Min(profileHealth, maxHealth) : maxHealth,
                [.. boosts.ActiveBoosts
                .Where(activeBoost => activeBoost is not null)
                .Where(activeBoost => activeBoost!.StartTime + activeBoost.Duration >= currentTime)
                .SelectMany(activeBoost => _catalog.ItemsCatalog.GetItem(activeBoost!.ItemId)!.BoostInfo!.Effects.Select(effect => new EffectInfo(activeBoost.StartTime + activeBoost.Duration, effect)))
                .Where(effectInfo => effectInfo.Effect.Type is CICIBIEType.ADVENTURE_XP or CICIBIEType.DEFENSE or CICIBIEType.EATING or CICIBIEType.HEALTH or CICIBIEType.MINING_SPEED or CICIBIEType.STRENGTH)
                .Select(effectInfo => new InitialPlayerStateResponse.BoostStatusEffect(
                    effectInfo.Effect.Type switch
                    {
                        CICIBIEType.ADVENTURE_XP => InitialPlayerStateResponse.BoostStatusEffectType.ADVENTURE_XP,
                        CICIBIEType.DEFENSE => InitialPlayerStateResponse.BoostStatusEffectType.DEFENSE,
                        CICIBIEType.EATING => InitialPlayerStateResponse.BoostStatusEffectType.EATING,
                        CICIBIEType.HEALTH => InitialPlayerStateResponse.BoostStatusEffectType.HEALTH,
                        CICIBIEType.MINING_SPEED => InitialPlayerStateResponse.BoostStatusEffectType.MINING_SPEED,
                        CICIBIEType.STRENGTH => InitialPlayerStateResponse.BoostStatusEffectType.STRENGTH,
                        _ => throw new UnreachableException(),
                    },
                    effectInfo.Effect.Value,
                    effectInfo.EndTime - currentTime
                ))]
            );
        }
    }

#pragma warning disable IDE0060 // Remove unused parameter
    private async Task<InventoryResponse?> HandleGetInventoryAsync(Guid instanceId, Guid requestedInventoryAccountId, CancellationToken cancellationToken = default)
#pragma warning restore IDE0060 // Remove unused parameter
    {
        await using var earthDb = await _earthDbFactory.CreateDbContextAsync(cancellationToken);

        var stackableItems = earthDb.StackableItems
            .AsNoTracking()
            .Where(item => item.ProfileId == requestedInventoryAccountId)
            .AsAsyncEnumerable();

        var nonStackableItems = earthDb.NonStackableItems
            .AsNoTracking()
            .Where(item => item.ProfileId == requestedInventoryAccountId)
            .AsAsyncEnumerable();

        var hotbar = await earthDb.Hotbars
            .AsNoTracking()
            .FirstAsync(hotbar => hotbar.Id == requestedInventoryAccountId, cancellationToken: cancellationToken);

        return new InventoryResponse(
            await AsyncEnumerable.Concat(
                stackableItems
                    .Select(item => new InventoryResponseItem(item.ItemId, item.Count, null, 0)),
                nonStackableItems
                    .Select(instance => new InventoryResponseItem(instance.ItemId, 1, instance.InstanceId, instance.Wear))
            ).Where(item => item.Count > 0).ToArrayAsync(cancellationToken),
            [.. hotbar.Items.Select(item => item is { Count: > 0 } ? new InventoryResponseHotbarItem(item.Uuid, item.Count, item.InstanceId) : null)]
        );
    }

#pragma warning disable IDE0060 // Remove unused parameter
    private async Task<bool> HandleInventoryAddAsync(Guid instanceId, InventoryAddItemMessage inventoryAddItemMessage, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
#pragma warning restore IDE0060 // Remove unused parameter
    {
        var catalogItem = _catalog.ItemsCatalog.GetItem(inventoryAddItemMessage.ItemId);
        if (catalogItem is null)
        {
            return false;
        }

        if (!catalogItem.Stackable && inventoryAddItemMessage.InstanceId is null)
        {
            return false;
        }

        await using var earthDb = await _earthDbFactory.CreateDbContextAsync(cancellationToken);

        if (catalogItem.Stackable)
        {
            await InventoryUtils.AddStackableItemsAsync(earthDb, ResultsEF.Builder.Null, inventoryAddItemMessage.PlayerId, inventoryAddItemMessage.ItemId, inventoryAddItemMessage.Count, cancellationToken);
        }
        else
        {
            await InventoryUtils.AddInstanceItemsAsync(earthDb, ResultsEF.Builder.Null, [new NonStackableItemInstanceEF(inventoryAddItemMessage.PlayerId, inventoryAddItemMessage.ItemId, inventoryAddItemMessage.InstanceId!.Value, inventoryAddItemMessage.Wear)], cancellationToken);
        }

        var journalItemUnlocked = false;
        if (await JournalUtils.AddCollectedItemAsync(earthDb, ResultsEF.Builder.Null, inventoryAddItemMessage.PlayerId, inventoryAddItemMessage.ItemId, timestamp, inventoryAddItemMessage.Count, cancellationToken) == 0)
        {
            if (catalogItem.JournalEntry is not null)
            {
                journalItemUnlocked = true;
            }
        }

        await earthDb.SaveChangesAsync(cancellationToken);

        if (journalItemUnlocked)
        {
            await TokenUtils.AddTokenAsync(earthDb, ResultsEF.Builder.Null, new JournalItemUnlockedTokenEF(inventoryAddItemMessage.PlayerId, inventoryAddItemMessage.ItemId), cancellationToken);
        }

        return true;
    }

    private async Task<object> HandleInventoryRemoveAsync(Guid instanceId, InventoryRemoveItemRequest inventoryRemoveItemRequest, CancellationToken cancellationToken = default)
    {
        await using var earthDb = await _earthDbFactory.CreateDbContextAsync(cancellationToken);

        var hotbar = await earthDb.Hotbars
            .AsTracking()
            .FirstAsync(hotbar => hotbar.Id == inventoryRemoveItemRequest.PlayerId, cancellationToken: cancellationToken);

        object result;
        if (inventoryRemoveItemRequest.InstanceId is not null)
        {
            if (await InventoryUtils.TakeInstanceItemsAsync(earthDb, ResultsEF.Builder.Null, inventoryRemoveItemRequest.PlayerId, inventoryRemoveItemRequest.ItemId, [inventoryRemoveItemRequest.InstanceId.Value], cancellationToken) is null)
            {
                LogBuildplateInstanceAttemptedToRemoveItemFromPlayerThatIsNotInInventory(instanceId, inventoryRemoveItemRequest.ItemId, inventoryRemoveItemRequest.InstanceId.Value.ToString(), inventoryRemoveItemRequest.PlayerId);
                result = false;
            }
            else
            {
                result = true;
            }
        }
        else
        {
            if (await InventoryUtils.TakeStackableItemsAsync(earthDb, ResultsEF.Builder.Null, inventoryRemoveItemRequest.PlayerId, inventoryRemoveItemRequest.ItemId, inventoryRemoveItemRequest.Count, cancellationToken))
            {
                result = inventoryRemoveItemRequest.Count;
            }
            else
            {
                var count = (await earthDb.StackableItems
                    .AsNoTracking()
                    .FirstOrDefaultAsync(item => item.ProfileId == inventoryRemoveItemRequest.PlayerId && item.ItemId == inventoryRemoveItemRequest.ItemId, cancellationToken))?.Count ?? 0;
                if (!await InventoryUtils.TakeStackableItemsAsync(earthDb, ResultsEF.Builder.Null, inventoryRemoveItemRequest.PlayerId, inventoryRemoveItemRequest.ItemId, count, cancellationToken))
                {
                    count = 0;
                }

                LogBuildplateInstanceAttemptedToRemoveItemFromPlayerThatIsNotInInventory(instanceId, inventoryRemoveItemRequest.ItemId, (inventoryRemoveItemRequest.Count - count).ToString(CultureInfo.InvariantCulture), inventoryRemoveItemRequest.PlayerId);
                result = count;
            }
        }

        await HotbarUtils.LimitToInventoryAsync(earthDb, inventoryRemoveItemRequest.PlayerId, hotbar, cancellationToken);

        await earthDb.SaveChangesAsync(cancellationToken);

        return result;
    }

    private async Task<bool> HandleInventoryUpdateWearAsync(Guid instanceId, InventoryUpdateItemWearMessage inventoryUpdateItemWearMessage, CancellationToken cancellationToken = default)
    {
        await using var earthDb = await _earthDbFactory.CreateDbContextAsync(cancellationToken);

        var nonStackableItemInstance = await earthDb.NonStackableItems
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.ProfileId == inventoryUpdateItemWearMessage.PlayerId && item.ItemId == inventoryUpdateItemWearMessage.ItemId && item.InstanceId == inventoryUpdateItemWearMessage.InstanceId, cancellationToken);

        if (nonStackableItemInstance is not null)
        {
            // TODO: make NonStackableItemInstance mutable instead of doing this
            if (await InventoryUtils.TakeInstanceItemsAsync(earthDb, ResultsEF.Builder.Null, inventoryUpdateItemWearMessage.PlayerId, inventoryUpdateItemWearMessage.ItemId, [inventoryUpdateItemWearMessage.InstanceId], cancellationToken) is null)
            {
                throw new InvalidOperationException();
            }

            await InventoryUtils.AddInstanceItemsAsync(earthDb, ResultsEF.Builder.Null, [new NonStackableItemInstanceEF(inventoryUpdateItemWearMessage.PlayerId, inventoryUpdateItemWearMessage.ItemId, inventoryUpdateItemWearMessage.InstanceId, inventoryUpdateItemWearMessage.Wear)], cancellationToken);

        }
        else
        {
            LogBuildplateInstanceAttemptedToUpdateItemWearForItemPlayerThatIsNotInInventory(instanceId, inventoryUpdateItemWearMessage.ItemId, inventoryUpdateItemWearMessage.InstanceId, inventoryUpdateItemWearMessage.PlayerId);
        }

        return true;
    }

#pragma warning disable IDE0060 // Remove unused parameter
    private async Task<bool> HandleInventorySetHotbarAsync(Guid instanceId, InventorySetHotbarMessage inventorySetHotbarMessage, CancellationToken cancellationToken = default)
#pragma warning restore IDE0060 // Remove unused parameter
    {
        await using var earthDb = await _earthDbFactory.CreateDbContextAsync(cancellationToken);

        var hotbar = await earthDb.Hotbars
            .AsTracking()
            .FirstAsync(hotbar => hotbar.Id == inventorySetHotbarMessage.PlayerId, cancellationToken: cancellationToken);

        for (var index = 0; index < hotbar.Items.Length; index++)
        {
            var item = inventorySetHotbarMessage.Items[index];
            hotbar.Items[index] = item is not null ? new HotbarEF.Item(item.ItemId, item.Count, item.InstanceId) : null;
        }

        await HotbarUtils.LimitToInventoryAsync(earthDb, inventorySetHotbarMessage.PlayerId, hotbar, cancellationToken);

        await earthDb.SaveChangesAsync(cancellationToken);

        return true;
    }

    private static RequestWithInstanceId<T>? ReadRequest<T>(string str)
    {
        try
        {
            var request = Json.Deserialize<RequestWithInstanceId<T>>(str);
            if (request is null || request.InstanceId == Guid.Empty || EqualityComparer<T>.Default.Equals(request.Request, default))
            {
                return null;
            }

            return request;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static T? ReadRawRequest<T>(string str, ILogger logger)
    {
        try
        {
            var request = Json.Deserialize<T>(str);
            return request;
        }
        catch (Exception exception)
        {
            LogBadJsonInBuildplatesEventBusRequest(logger, exception);
            return default;
        }
    }

    private sealed record RequestWithInstanceId<T>(
        Guid InstanceId,
        T Request
    );

    [LoggerMessage(Level = LogLevel.Error, Message = "Database error while handling request")]
    private partial void LogDatabaseErrorWhileHandlingRequest(Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Buildplates event bus request handler error")]
    private partial void LogBuildplatesEventBusRequestHandlerError(Exception? exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "World data object {ServerDataObjectId} for buildplate {BuildplateId} could not be loaded from object store")]
    private partial void LogFailedToGetServerData(Guid ServerDataObjectId, Guid BuildplateId);

    [LoggerMessage(Level = LogLevel.Error, Message = "World data object {ServerDataObjectId} for shared buildplate {BuildplateId} could not be loaded from object store")]
    private partial void LogFailedToGetServerDataShared(Guid ServerDataObjectId, Guid BuildplateId);

    [LoggerMessage(Level = LogLevel.Error, Message = "World data object {ServerDataObjectId} for encounter buildplate {BuildplateId} could not be loaded from object store")]
    private partial void LogFailedToGetServerDataEncounter(Guid ServerDataObjectId, Guid BuildplateId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not generate preview for buildplate")]
    private partial void LogCouldNotGeneratePreviewForBuildplate();

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to store new world data object for buildplate {BuildplateId} in object store")]
    private partial void LogFailedToStoreData(Guid BuildplateId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to store new preview object for buildplate {BuildplateId} in object store")]
    private partial void LogFailedToStorePreview(Guid BuildplateId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stored new snapshot for buildplate {BuildplateId}")]
    private partial void LogStoredNewSnapshotForBuildplate(Guid BuildplateId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error saving world")]
    private partial void LogErrorSavingWorld(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unknown instance type '{Type}' in HandlePlayerConnected")]
    private partial void LogExpectedBackpackContentsInPlayerDisconnectedRequest(BuildplateInstancesManager.InstanceType Type);

    [LoggerMessage(Level = LogLevel.Error, Message = "Expected backpack contents in player disconnected request")]
    private partial void LogExpectedBackpackContentsInPlayerDisconnectedRequest();

    [LoggerMessage(Level = LogLevel.Error, Message = "Backpack contents contained item that is not in item catalog")]
    private partial void LogBackpackContentsContainedItemThatIsNotInItemCatalog();

    [LoggerMessage(Level = LogLevel.Error, Message = "Backpack contents contained non-stackable item without instance ID")]
    private partial void LogBackpackContentsContainedNonStackableItemWithoutInstanceId();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Buildplate instance {InstanceId} attempted to remove item {ItemId} {ItemInstanceOrCount} from player {AccountId} that is not in inventory")]
    private partial void LogBuildplateInstanceAttemptedToRemoveItemFromPlayerThatIsNotInInventory(Guid InstanceId, Guid ItemId, string ItemInstanceOrCount, Guid AccountId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Buildplate instance {InstanceId} attempted to update item wear for item {ItemId} {ItemInstanceId} player {AccountId} that is not in inventory")]
    private partial void LogBuildplateInstanceAttemptedToUpdateItemWearForItemPlayerThatIsNotInInventory(Guid InstanceId, Guid ItemId, Guid ItemInstanceId, Guid AccountId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Bad JSON in buildplates event bus request")]
    private static partial void LogBadJsonInBuildplatesEventBusRequest(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Handling buildplates request: {RequestType}")]
    private partial void LogHandlingBuildplatesRequest(string RequestType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Handling playerConnected request. Raw payload: {Payload}")]
    private partial void LogHandlingPlayerConnectedRawPayload(string Payload);

    [LoggerMessage(Level = LogLevel.Debug, Message = "playerConnected request has explicit InstanceId {InstanceId}")]
    private partial void LogPlayerConnectedExplicitInstanceId(Guid InstanceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to parse PlayerConnectedRequest from raw payload: {Payload}")]
    private partial void LogFailedToParsePlayerConnectedRequest(string Payload);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not resolve InstanceId for player {PlayerUuid}")]
    private partial void LogCouldNotResolveInstanceIdForPlayer(Guid PlayerUuid);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Resolved InstanceId {InstanceId} for player {PlayerUuid}")]
    private partial void LogResolvedInstanceIdForPlayer(Guid InstanceId, Guid PlayerUuid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "HandlePlayerConnectedAsync returned null for instance {InstanceId}, player {PlayerUuid}")]
    private partial void LogHandlePlayerConnectedReturnedNull(Guid InstanceId, Guid? PlayerUuid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "HandlePlayerConnectedAsync: GetInstanceInfo({InstanceId}) returned null")]
    private partial void LogPlayerConnectedGetInstanceInfoReturnedNull(Guid InstanceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Host player {PlayerId} disconnected from instance {InstanceId}, stopping instance")]
    private partial void LogHostPlayerDisconnectedStoppingInstance(Guid PlayerId, Guid InstanceId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to stop instance {InstanceId} after host disconnect")]
    private partial void LogFailedToStopInstanceAfterHostDisconnect(Exception ex, Guid InstanceId);
}
