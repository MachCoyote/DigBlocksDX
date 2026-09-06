# Chunk implementation progress

## September 6, 2026: block registry

Completed `Assets/_Project/Scripts/Voxels/BlockRegistry.cs` against the existing
`Assets/_Project/Tests/EditMode/Voxels/BlockRegistryTests.cs` contract.

- Definitions validate lowercase namespaced identifiers and canonicalize finite
  state properties and tags with ordinal ordering. Input collections are detached.
- Solid air and fluid empty occupy ID zero; remaining states receive ordinal,
  dense uint IDs. Unknown keys and out-of-range IDs fail explicitly.
- SHA-256 compatibility fingerprints include both channel boundaries, state keys,
  behavior/model keys, fluid permissions and tags, with length-prefixed fields.
- Dummy definitions provide air, stone, empty fluid and water.

Verification: all 16 existing NUnit cases failed against the stub and then passed.
Fresh Unity 6000.6.0f1 EditMode execution also passed 16/16 in an isolated project
containing exact copies of the source/tests. No C# compiler errors or warnings.
This verifies registry behavior and Unity compatibility, not the full game assembly
graph. Temporary evidence: `.utmp/registry-verification/results.xml` and `editor.log`.

The registry commit includes its existing tests and the minimal Unity assembly
and asset metadata needed to compile those files. Other pre-existing storage,
networking, bootstrap, settings, texture and workflow changes remain outside it.

## Remaining implementation

Next: versioned transfer frames and bounded reassembly using the approved
independent Unity Transport companion connection. Then strengthen storage/job
lifetime coverage before world residency, admission binding, streaming schedules,
applied acknowledgements, and full integration verification.

The approved carrier uses IPC for singleplayer and a configurable second UDP port
for remote peers. Chunk payloads do not use NetCode RPC queues. No new major
architectural decisions have been made in this resumption.
