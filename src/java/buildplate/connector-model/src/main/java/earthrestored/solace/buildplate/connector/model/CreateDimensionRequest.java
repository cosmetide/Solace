package earthrestored.solace.buildplate.connector.model;

import org.jetbrains.annotations.NotNull;

public record CreateDimensionRequest(
		@NotNull String instanceId,
		@NotNull String serverDataBase64,
		boolean survival,
		boolean night,
		boolean saveEnabled,
		@NotNull InventoryType inventoryType) {
}
