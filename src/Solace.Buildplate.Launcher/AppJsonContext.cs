using System.Text.Json.Serialization;
using Solace.Buildplate.Connector.Model;
using Solace.Buildplate.Model;

namespace Solace.Buildplate.Launcher;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(bool?))]
[JsonSerializable(typeof(Buildplate.PreviewGenerator.PreviewModel))]
[JsonSerializable(typeof(ConnectorPluginArg))]
[JsonSerializable(typeof(CreateDimensionRequest))]
[JsonSerializable(typeof(UnloadDimensionRequest))]
[JsonSerializable(typeof(FindPlayerIdRequest))]
[JsonSerializable(typeof(InitialPlayerStateResponse))]
[JsonSerializable(typeof(InstanceManager.BuildplateLoadRequest))]
[JsonSerializable(typeof(InstanceManager.BuildplateLoadResponse))]
[JsonSerializable(typeof(InstanceManager.EncounterBuildplateLoadRequest))]
[JsonSerializable(typeof(InstanceManager.SharedBuildplateLoadRequest))]
[JsonSerializable(typeof(InstanceManager.StartNotification))]
[JsonSerializable(typeof(InstanceManager.StartRequest))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(InventoryAddItemMessage))]
[JsonSerializable(typeof(InventoryRemoveItemRequest))]
[JsonSerializable(typeof(InventoryResponse))]
[JsonSerializable(typeof(InventorySetHotbarMessage))]
[JsonSerializable(typeof(InventoryUpdateItemWearMessage))]
[JsonSerializable(typeof(PlayerConnectedRequest))]
[JsonSerializable(typeof(PlayerConnectedResponse))]
[JsonSerializable(typeof(PlayerDisconnectedRequest))]
[JsonSerializable(typeof(PlayerDisconnectedResponse))]
[JsonSerializable(typeof(PreviewRequest))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(WorldSavedMessage))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}
