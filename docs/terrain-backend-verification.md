# Terrain rendering backend verification

September 8, 2026. Unity 6000.6.0f1, URP 17.6, NVIDIA GeForce RTX 3080 Ti on Windows.

## Results

The isolated GPU probe passed on the project's running DX12 editor and on a separate Vulkan batchmode project using the same installed URP packages and PC render settings. The Vulkan run kept graphics enabled; a nographics compile cannot verify this path.

| Contract | DX12 | Vulkan |
| --- | --- | --- |
| Structured uint3 records with 12-byte CPU/GPU stride | Passed | Passed |
| Mapped reusable staging buffer to GPU geometry buffer via compute copy | Passed | Passed |
| GPU visible-reference append and indirect instance-count copy | Two of three candidates, as expected | Two of three candidates, as expected |
| One procedural indirect command renders both visible quads | Passed | Passed |
| Texture-array layer selection, quarter-turn orientation, repeating UVs | Passed | Passed |
| Second update reuses the same GPU allocations | Passed | Passed |
| Completion protection | Pollable graphics fence | Async readback token |
| Actual PC URP lighting, procedural and indexed indirect paths | 7,311 light-responsive pixels each | 7,311 each |
| Actual URP shadow casting and receiving | 961 shadow-responsive pixels each | 962 each |

The lighting comparison renders with a white versus black main light. The shadow comparison renders the same geometry with casting disabled versus enabled. Initial shadow coverage was inconclusive because the wall hid its projected shadow from the camera; changing the light direction exposed a substantial shadow. A missing `_CLUSTER_LIGHT_LOOP` shader variant initially suppressed main-light attenuation on the PC renderer; the probe now includes the installed URP keyword.

## Architectural consequences

Use packed quads and built-in indirect batching. One command represents a material batch containing quads from many chunks; do not create one command per chunk and assume Unity combines them into hardware multi-draw. Both indexed and procedural variants work functionally; no performance winner is claimed by this small probe.

The selected 96-bit quad layout reserves 6 bits for each local anchor coordinate, 5 each for width-minus-one and height-minus-one, 3 for direction, 16 for texture layer, 8 for tint, 2 for rotation, 24 for the owning chunk slot and 8 for material identity. Material partitions batches. The remaining geometry/surface bits stay reserved. This stores geometry once per quad, not four or six generic floating-point vertices. Visible references and chunk metadata consume additional memory and must be counted in profiling.

Use mapped staging plus reusable GPU geometry storage. Mapping alone does not establish where a driver places memory. The compute copy avoids requiring partial native buffer-copy APIs or retaining all geometry in a CPU-visible allocation.

**Vulkan fence caveat:** `supportsGraphicsFence` is true on this configuration while `supportsAsyncCompute` is false. Reading `GraphicsFence.passed` on an AsyncQueueSynchronisation fence throws NotSupportedException. Capability selection must check both and use an ordered async readback completion token when queue-fence polling is unavailable. Production polls completion without waiting and retains retired allocations until completion; fixed frame delays are not a substitute. The probe deliberately waits/reads back to assert results, and those blocking calls must not enter ordinary streaming.

Graphics.RenderPrimitivesIndirect participates in URP forward lighting and shadow casting with explicit shader passes. Camera-frustum culling must not remove off-camera shadow casters: build separate visibility lists or use a conservative shadow candidate list for a ShadowsOnly batch. The final shader also needs depth/normal passes consistent with the active URP features.

## Reproduction and limits

Use **DigBlocks > Verification > Terrain GPU backend**. The editor-only entry point is `Assets/_Project/Scripts/Client/Rendering/Editor/TerrainBackendProbe.cs`; fixtures live under `Assets/_Project/Tests/EditMode/ClientRendering/Fixtures/`. The probe creates transient objects and render targets, restores the main light/render target, and writes evidence to `.utmp/terrain-backend-probe.txt` and PNGs. Batch invocation uses `-executeMethod DigBlocks.Client.Rendering.Editor.TerrainBackendProbe.Run` with graphics enabled; failed assertions exit nonzero.

The alternate-API project is `.utmp/TerrainGraphicsProbe`, with its own Library and no game scene changes. Its report and PNGs are under its `.utmp/`; the driver log is `.utmp/terrain-vulkan.log` in the main project.

These are functional feasibility results, not streaming benchmarks. The tiny compute fixture filters by a simple visibility boundary, not a complete chunk frustum implementation. No native hardware MDI claim, frame-time target, allocator stress result, full mesher result, production gameplay result, or support for other devices/APIs is implied. Measure representative chunk counts, fragmented geometry, upload pressure, render passes and GPU timings after integration.
