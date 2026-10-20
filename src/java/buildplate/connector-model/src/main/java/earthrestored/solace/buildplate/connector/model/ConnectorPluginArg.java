package earthrestored.solace.buildplate.connector.model;

import org.jetbrains.annotations.NotNull;
import org.jetbrains.annotations.Nullable;

public record ConnectorPluginArg(
		@NotNull String eventBusAddress,
		@NotNull String eventBusQueueName,
		@Nullable InventoryType inventoryType) {
	public ConnectorPluginArg(@NotNull String eventBusAddress, @NotNull String eventBusQueueName) {
		this(eventBusAddress, eventBusQueueName, null);
	}
}