package earthrestored.solace.buildplate.connector.model;

import org.jetbrains.annotations.NotNull;
import org.jetbrains.annotations.Nullable;

public record PlayerInstanceInfoResponse(
		@NotNull String instanceId,
		@NotNull InventoryType inventoryType,
		boolean survival,
		boolean night) {
}
