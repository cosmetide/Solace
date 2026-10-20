package earthrestored.solace.buildplate.connector.model;

import org.jetbrains.annotations.NotNull;

public record UnloadDimensionRequest(
		@NotNull String instanceId,
		boolean save) {
}
