package earthrestored.solace.buildplate.connector.model;

import org.jetbrains.annotations.NotNull;

public record RequestWithInstanceId<T>(
		@NotNull String instanceId,
		@NotNull T request) {
}
