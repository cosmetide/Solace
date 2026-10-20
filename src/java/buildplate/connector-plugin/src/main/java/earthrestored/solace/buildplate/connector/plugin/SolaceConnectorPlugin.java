package earthrestored.solace.buildplate.connector.plugin;

import com.google.gson.Gson;
import org.jetbrains.annotations.NotNull;

import micheal65536.fountain.connector.plugin.ConnectorPlugin;
import micheal65536.fountain.connector.plugin.DisconnectResponse;
import micheal65536.fountain.connector.plugin.Inventory;
import micheal65536.fountain.connector.plugin.Logger;
import micheal65536.fountain.connector.plugin.PlayerLoginInfo;
import earthrestored.solace.buildplate.connector.model.ConnectorPluginArg;
import earthrestored.solace.buildplate.connector.model.InventoryResponse;
import earthrestored.solace.buildplate.connector.model.InventoryType;
import earthrestored.solace.buildplate.connector.model.PlayerConnectedRequest;
import earthrestored.solace.buildplate.connector.model.PlayerConnectedResponse;
import earthrestored.solace.buildplate.connector.model.PlayerDisconnectedRequest;
import earthrestored.solace.buildplate.connector.model.PlayerDisconnectedResponse;
import earthrestored.solace.buildplate.connector.model.RequestWithInstanceId;
import earthrestored.solace.eventbus.client.EventBusClient;
import earthrestored.solace.eventbus.client.Publisher;
import earthrestored.solace.eventbus.client.RequestSender;

import java.util.HashMap;

public final class SolaceConnectorPlugin implements ConnectorPlugin {
	private String queueName;
	private EventBusClient eventBusClient;
	private Publisher publisher;
	private RequestSender requestSender;

	private final HashMap<String, InventoryType> playerInventoryTypes = new HashMap<>();
	private final HashMap<String, String> playerInstanceIds = new HashMap<>();
	private final HashMap<String, PlayerInventory> playerInventories = new HashMap<>();

	@Override
	public void init(@NotNull String arg, @NotNull Logger logger) throws ConnectorPluginException {
		ConnectorPluginArg connectorPluginArg;
		try {
			connectorPluginArg = new Gson().fromJson(arg, ConnectorPluginArg.class);
		} catch (Exception exception) {
			throw new ConnectorPluginException("Invalid connector plugin arg string");
		}

		ClassLoader originalClassLoader = Thread.currentThread().getContextClassLoader();
		try {
			Thread.currentThread().setContextClassLoader(this.getClass().getClassLoader());

			this.eventBusClient = EventBusClient.connectAsync(connectorPluginArg.eventBusAddress());

		} catch (Exception exception) {
			throw new ConnectorPluginException(exception);
		} finally {
			Thread.currentThread().setContextClassLoader(originalClassLoader);
		}

		this.queueName = connectorPluginArg.eventBusQueueName();
		this.publisher = this.eventBusClient.addPublisher();
		this.requestSender = this.eventBusClient.addRequestSender();
	}

	@Override
	public void shutdown() throws ConnectorPluginException {
		this.eventBusClient.close();
	}

	@Override
	public boolean onPlayerConnected(@NotNull PlayerLoginInfo playerLoginInfo) throws ConnectorPluginException {
		PlayerConnectedResponse playerConnectedResponse = EventBusHelper.doRequestResponseSync(this.requestSender,
				this.queueName, "playerConnected", new PlayerConnectedRequest(playerLoginInfo.uuid, ""),
				PlayerConnectedResponse.class); // TODO: join code
		if (playerConnectedResponse == null || !playerConnectedResponse.accepted()) {
			return false;
		}
		InventoryType inventoryType = playerConnectedResponse.inventoryType() != null
				? playerConnectedResponse.inventoryType()
				: InventoryType.DISCARD;
		String instanceId = playerConnectedResponse.instanceId() != null
				? playerConnectedResponse.instanceId()
				: "";

		if (inventoryType != InventoryType.SYNCED && playerConnectedResponse.initialInventoryContents() == null) {
			throw new ConnectorPluginException(
					"Bad player connected response data (missing initial inventory data for inventory type %s)"
							.formatted(inventoryType.name()));
		}

		this.playerInventoryTypes.put(playerLoginInfo.uuid, inventoryType);
		this.playerInstanceIds.put(playerLoginInfo.uuid, instanceId);
		this.playerInventories.put(playerLoginInfo.uuid, switch (inventoryType) {
			case SYNCED ->
				new SyncedPlayerInventory(playerLoginInfo.uuid, instanceId, this.requestSender, this.publisher, this.queueName);
			case DISCARD -> new DiscardPlayerInventory(playerConnectedResponse.initialInventoryContents());
			case BACKPACK -> new BackpackPlayerInventory(playerConnectedResponse.initialInventoryContents());
		});
		return true;
	}

	@Override
	@NotNull
	public DisconnectResponse onPlayerDisconnected(@NotNull String playerId, float health)
			throws ConnectorPluginException {
		InventoryType inventoryType = this.playerInventoryTypes.getOrDefault(playerId, InventoryType.DISCARD);
		InventoryResponse backpackContents = switch (inventoryType) {
			case SYNCED -> null;
			case DISCARD -> null;
			case BACKPACK ->
				((BackpackPlayerInventory) this.getInventoryForPlayer(playerId)).getContentsAsInventoryResponse();
		};
		String instanceId = this.playerInstanceIds.getOrDefault(playerId, "");
		PlayerDisconnectedRequest playerDisconnectedRequest = new PlayerDisconnectedRequest(playerId, backpackContents);
		PlayerDisconnectedResponse playerDisconnectedResponse = EventBusHelper.doRequestResponseSync(this.requestSender,
				this.queueName, "playerDisconnected", new RequestWithInstanceId<>(instanceId, playerDisconnectedRequest), PlayerDisconnectedResponse.class);
		this.playerInventories.remove(playerId);
		this.playerInventoryTypes.remove(playerId);
		this.playerInstanceIds.remove(playerId);
		return new DisconnectResponse();
	}

	@Override
	public boolean onPlayerDead(@NotNull String playerId) throws ConnectorPluginException {
		String instanceId = this.playerInstanceIds.getOrDefault(playerId, "");
		Boolean respawn = EventBusHelper.doRequestResponseSync(this.requestSender, this.queueName, "playerDead",
				new RequestWithInstanceId<>(instanceId, playerId), Boolean.class);
		return respawn != null && respawn;
	}

	@Override
	@NotNull
	public Inventory onPlayerGetInventory(@NotNull String playerId) throws ConnectorPluginException {
		return this.getInventoryForPlayer(playerId).getContents();
	}

	@Override
	public void onPlayerInventoryAddItem(@NotNull String playerId, @NotNull String itemId, int count)
			throws ConnectorPluginException {
		this.getInventoryForPlayer(playerId).addItem(itemId, count);
	}

	@Override
	public void onPlayerInventoryAddItem(@NotNull String playerId, @NotNull String itemId, @NotNull String instanceId,
			int wear) throws ConnectorPluginException {
		this.getInventoryForPlayer(playerId).addItem(itemId, instanceId, wear);
	}

	@Override
	public int onPlayerInventoryRemoveItem(@NotNull String playerId, @NotNull String itemId, int count)
			throws ConnectorPluginException {
		return this.getInventoryForPlayer(playerId).removeItem(itemId, count);
	}

	@Override
	public boolean onPlayerInventoryRemoveItem(@NotNull String playerId, @NotNull String itemId,
			@NotNull String instanceId) throws ConnectorPluginException {
		return this.getInventoryForPlayer(playerId).removeItem(itemId, instanceId);
	}

	@Override
	public void onPlayerInventoryUpdateItemWear(@NotNull String playerId, @NotNull String itemId,
			@NotNull String instanceId, int wear) throws ConnectorPluginException {
		this.getInventoryForPlayer(playerId).updateItemWear(itemId, instanceId, wear);
	}

	@Override
	public void onPlayerInventorySetHotbar(@NotNull String playerId, Inventory.HotbarItem[] hotbar)
			throws ConnectorPluginException {
		this.getInventoryForPlayer(playerId).setHotbar(hotbar);
	}

	@NotNull
	private PlayerInventory getInventoryForPlayer(@NotNull String playerId) throws ConnectorPluginException {
		PlayerInventory playerInventory = this.playerInventories.getOrDefault(playerId, null);
		if (playerInventory == null) {
			throw new ConnectorPluginException("Inventory does not exist for player ID (player not connected?)");
		}
		return playerInventory;
	}
}