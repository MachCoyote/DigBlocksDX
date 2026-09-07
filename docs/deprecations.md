# Deprecations

## Managed ECS components (Unity 6.6)

`EntityManager.AddComponentObject/GetComponentObject/SetComponentObject` are
deprecated in Entities 6.6. Session control-plane state is held by
`SessionContextSystem`, a managed adapter system; the unmanaged `SessionActive`
marker gates the protocol systems. Keep gameplay data in unmanaged components.
Do not reintroduce a managed `IComponentData` merely to share a service reference.

## Unity object discovery (August 2026)

`FindObjectsSortMode` and the `Object.FindObjectsByType<T>(FindObjectsSortMode)` overload are deprecated. Use `Object.FindObjectsByType<T>()` when inactive objects do not need to be included, or `Object.FindObjectsByType<T>(FindObjectsInactive)` when they do.

The removed sort option relied on `InstanceID` ordering. Unity plans to replace Instance IDs with Entity IDs, so that prior ordering cannot be retained. Tests and runtime code must assert behavior without depending on discovery order.
