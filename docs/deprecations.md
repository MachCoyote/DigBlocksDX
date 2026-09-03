# Deprecations

## Unity object discovery (August 2026)

`FindObjectsSortMode` and the `Object.FindObjectsByType<T>(FindObjectsSortMode)` overload are deprecated. Use `Object.FindObjectsByType<T>()` when inactive objects do not need to be included, or `Object.FindObjectsByType<T>(FindObjectsInactive)` when they do.

The removed sort option relied on `InstanceID` ordering. Unity plans to replace Instance IDs with Entity IDs, so that prior ordering cannot be retained. Tests and runtime code must assert behavior without depending on discovery order.
