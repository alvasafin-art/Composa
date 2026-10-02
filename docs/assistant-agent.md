# Document agent: tools, execution and verification

Composa's chat agent and external MCP clients use the same native `ComposaTools` methods. Tool names, full descriptions, read-only annotations and input schemas come from `EditorOperationCatalog`. New editor capabilities are added to the native API, not reimplemented as chat-specific commands or keyword-specific scripts.

The design follows the [MCP tools specification](https://modelcontextprotocol.io/specification/2025-11-25/server/tools), [official function-tool design guidance](https://developers.openai.com/api/docs/guides/function-calling) and [evaluation best practices](https://developers.openai.com/api/docs/guides/evaluation-best-practices). MCP is the interoperability contract; it does not by itself make a model understand a document or guarantee task completion.

## Execution contract

1. `begin_task` declares edit/inspect intent and the user's goal. Intent is model-classified, not inferred from Russian/English verbs. Inspect mode forbids mutations and cannot escalate to edit. The host persists this state and a bounded operation journal outside conversation history; `begin_task` disappears after declaration. Context compaction cannot erase the plan or restart completed actions. Final assertions are deliberately not guessed here: conversions can replace input layers with new output ids.
2. `get_document_state` reads typed canvas, selection, ruler guides and live layer properties. Layers are paged in panel top-to-bottom order; follow `nextOffset` while `hasMore`. Edits target full ids or unique names; ambiguous names are rejected by the inspector. A selection is canvas-space, whereas persistent masks belong to layers.
3. Common native tools are immediately available. `search_operations` retrieves up to four relevant native schemas and exposes their tools on the next step. `list_operations(name)` does the same for one exact operation. The discovery cache is bounded, avoiding an enormous all-tools prompt. The old `editor_operation` wrapper remains compatible.
4. Each command has a savepoint; failures restore that command's document state. Successful commands return valid JSON receipts with execution status, real changed/added/removed layer ids and actual document data. Schemas and JSON results are never cut mid-object. History compaction removes whole assistant/tool groups.
5. `verify_document` checks explicit postconditions against the document, without editing. Each condition specifies `layer` (full id/unique name, or null for document), a `property` from the schema enum, an operator (`equals`, `near`, `exists`, `absent`) and a scalar `expected` value. For example, `{layer:null,property:"width",expected:600}` or `{layer:"Tile",property:"shape.fill",expected:"#FFFF00"}`. Targets and properties are explicit; there are no guessed JSON paths, array indices or executable assertions.
6. After editing, `verify_document` records final assertions on actual output ids. Declared editing tasks cannot finish without such checks. The host independently repeats them before commit, including when a later edit invalidates an earlier successful check. An ignored failed verification prevents completion and rolls pending edits back. An already-satisfied, verified request is a valid no-op with no Undo entry. A command receipt proves execution, not that the whole request was satisfied. Visual appearance additionally needs `render`/`sample_color`; structural checks cannot prove artistic quality or that the model interpreted every semantic nuance correctly.

`query_layers` and `batch_set_layers` share a declarative selector: native kind, shape kind, aspect ratio, name substring, tag and group scope. Filtering precedes panel ordering, then zero-based `start`/`step`. Arithmetic and selection run deterministically in the editor, not by mentally counting UUIDs in model text. Batch color/visibility/opacity/blend changes validate all targets before mutation and preserve unmatched layers, masks and geometry as one Undo step. The selector contains no executable expressions; unknown fields are rejected. These are general editor capabilities, not handlers for individual user phrases.

A completed edit is one Undo entry. Cancel, limits, repeated failing operations and failed final verification roll back the request. Script-only requests remain reviewable code artifacts, not automatic edits. Arbitrary files/processes/network are not exposed; images and text must be attached explicitly. Chat operations stay scoped to the current document.

The loop permits at most 32 planning steps, 96 calls and 16 calls per step. Long tasks should use native batch operations or the bounded JavaScript API. `get_script_api` loads the manual on demand instead of including it with every native call. Provider history compaction respects its context budget and retains whole call/result exchanges. Model JSON preserves Unicode rather than expanding text to escape sequences. With vision enabled, the latest native `render` image (including region/grid) is delivered as tool data; without vision, pixels are unavailable and the agent must use structural data and `sample_color`/`trace_edges`.

The scripting interpreter's memory budget excludes native editor callback allocations; native surfaces remain bounded by `DocumentLimits`. Statement/time/cancellation limits remain active. llama.cpp receives semantically equivalent empty-object schemas in place of unrestricted boolean `true` schemas; other providers retain the original contract.

## Acceptance suite

`AgentCapabilityTests` checks schema parity/discovery, live tool promotion, whole JSON receipts, target identity/pagination, batch filtering/order/stepping, all-target validation, independent final verification, repair/recheck, verified no-ops, inspect-only safety and script memory accounting. `McpTests` invokes inspection, verification, querying and batch editing through the real stdio bridge and per-test pipe. `get_script_api` returns the complete small reference in one call, without fragment pagination.

`AgentAcceptanceTests` runs the production agent against a real local model and grades editor data, not the model's summary:

| Level | Request | Mechanical acceptance |
| --- | --- | --- |
| Basic | Create a colored rectangle | Exact geometry/color, one live new shape, original canvas |
| Basic | Add inset ruler guides | Four real guide records, no line layers |
| Intermediate | Edit text within a group | Live text/color, preserved hierarchy and unchanged footer |
| Intermediate | Apply a selection as layer mask | Correct mask pixels, live shape preserved, selection cleared |
| Advanced | Recolor alternating squares in a large stack | Correct panel order, no unrelated rectangle changes, no new layers |
| Advanced | Convert two shapes to a smart object | Embedded editable shapes, preserved rendered pixels |

Every real edit also checks one-step Undo, exact pre-edit state restoration and Redo. Reports include failures and operation traces; a real-model run is separate from ordinary deterministic tests. Set `COMPOSA_LIVE_LLAMA_SERVER` and `COMPOSA_LIVE_LLAMA_MODEL`, then run the `AgentAcceptanceTests` filter. The test writes `artifacts/assistant-evals/live-acceptance.json` relative to the test process working directory (normally the test output directory).

Passing this finite suite is not a guarantee for all instructions or all models. More capable models can improve planning, but model changes do not fix wrong tool schemas, lost context or fake success. Add new *acceptance scenarios and native capabilities*, rather than new handlers for individual user phrases. Paid ComfyUI calls are not made by this suite.
