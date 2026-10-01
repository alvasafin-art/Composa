# Assistant chat

Open **Assistant** in the tool-options row or **AI > Assistant**. Type a question or describe an edit and press **Send** (Enter; Shift+Enter adds a line). The conversation survives closing/reopening the window during the same application session. **New chat** clears it; conversations are not written to disk.

The assistant can answer without changing the document. With **Apply requested edits** enabled, the chat is a native tool-using agent: it reads document structure, performs real editing commands, receives the actual resulting state and continues with the next requested action. Shapes, existing text, layer transforms, groups and masks have direct typed tools. The remaining painting, selection, adjustment and filter commands are discovered from the same schemas as Composa's MCP server. Complex batches can use the constrained scripting API, and AI operations use the configured ComfyUI service.

Each requested edit is one Undo step, even when it uses several native commands and scripts. Failed commands restore their own savepoint; the agent receives the real error and can correct it. **Stop**, provider failure, or exceeding 18 model steps / 48 commands rolls back the whole pending edit. **Applied** requires an actual document change, not just an assistant's claim. Expand **Operations** to inspect calls, results and errors. The agent is scoped to the document where the request started, and full layer ids distinguish duplicate names inside folders.

Explicit requests such as **write/create/send a script** return reusable code instead of running the agent, even with **Apply requested edits** enabled. The response has **Save script**, **Save to Library** and an optional manual **Apply edit** action. **Write a script and run it** explicitly requests execution. Script-only requests never expose editor tools; an unsolicited `execute_script` response is treated as code for review, not executed. Intent recognition covers common Russian/English phrasing, not arbitrary languages or ambiguous mixed requests.

Within one editing request, identical successful object-creation commands are not executed again, even if the model changes the call id or switches between a direct tool and `editor_operation`. Other repeated edits are suppressed while their resulting state is still current. Intentionally distinct objects should have distinct names/arguments or be created by one explicit script loop. Repeating the same failing command without changing its arguments or document state stops the run early and rolls back. This protects against model loops; it does not prove that every semantically similar command is a duplicate.

Disable **Apply requested edits** for the legacy script-review flow: **Apply edit**, **Save script**, **Save to Library**, **Edit Script** and **Copy**. Library scripts appear in the Scripts menu and can receive shortcuts. This flow retains one automatic script-repair attempt; manually applying a script does not silently request another model response.

## Attachments and context

The built-in operation guide distinguishes document metadata (real ruler guides), rendered lines, live text, selections and layer masks. `guides` and `measure_text` are direct native tools; current document context includes guide ids/axes/positions and lock/visibility state. Text commands fit the actual full layout inside the canvas by default, with an explicit overflow opt-out. Script input uses awaited native `ui.form`/`prompt`, not an invented browser window. This improves grounding but cannot guarantee that every model interprets every ambiguous request correctly; the Operations log and Undo remain available.

Use **Attach** or drag files into the chat. Up to six files can accompany a message; × removes unwanted attachments. Supported files include `.js`, `.ts`, `.txt`, `.md`, `.json`, `.csv`, `.yaml`, `.yml`, `.svg`, PNG, JPEG, WebP and BMP. Text files are limited to 256 KB. A script is sent as data, not executed simply because it was attached: explicitly ask to explain, adapt or run it.

`import_attachment` / `doc.addAttachedImage(index)` imports an explicitly attached image. `read_attachment` pages through text/script attachments when the initial prompt had to truncate them. Indices are zero-based across all attachments in the current message. No arbitrary filesystem-reading API is exposed.

Each request includes current document geometry, layer hierarchy, names, types, tags, text and transforms, plus the current scripting contract. Document context is bounded to 160 layers and 500 characters per text layer. Chat context includes up to 20 recent messages within 24,000 characters; individual messages are bounded to 8,000 and attachment text to 12,000 characters. Local requests reduce these limits to fit the configured context, reserving space for output and prioritizing current files over older messages. Truncation is marked. Text attachment context remains in subsequent turns; reattach images to import them in a later turn.

Enable **Send document preview and attached images** only for a server/model configured for vision. Images sent to the model have a maximum side of 1024 pixels; importing uses the original file. The preview is refreshed after edits. Without vision the agent can inspect numerical pixel colors and traced edges via native tools, but cannot visually recognize image contents.

## Providers

The ⚙ menu selects the provider:

- **Local llama.cpp** uses a GGUF model and the configured `llama-server`. Automatic startup is limited to loopback URLs. The Windows launcher discovers the supplied executable and model relative to the project; paths, context size and reply limit are configurable.
- **API (chat completions)** accepts an HTTP/HTTPS base URL or complete `/chat/completions` endpoint, a model ID and optional Bearer key. A root URL gets `/v1/chat/completions`; custom base paths are preserved. No local health/startup contract is required. This supports chat-completions-compatible APIs, not every vendor's distinct protocol.

An entered key remains only for the current application session and is never serialized. For a persistent credential, name an environment variable (default `COMPOSA_ASSISTANT_API_KEY`). JSON response format is opt-in for remote providers; ordinary text replies and fenced JavaScript fallbacks are also accepted.

Automatic native editing requires a provider supporting standard chat-completions `tools` / `tool_calls`. Its actual operation results are returned as `tool` messages. Local llama.cpp uses its Jinja tool template; remote APIs do not need a local health/startup contract. Remote providers receive document context, chat and attachment text. Images are sent only when vision is enabled. Model weights are never uploaded.

## Execution safety

Live shape colors are edited with the native `set_shape(layer,color)` operation or `layer.setShapeColor(color)` / `layer.fill(color)` in scripts, without rasterization. Shape context contains actual kind, fill color and geometry. For every-other-layer edits use panel order (the reverse of the bottom-to-top document list), actual layer ids, and preserve unrelated layers. An editing request with no real changes is reported as a failure after one corrective retry, not as a successful prose-only action.

Scripts use existing editor commands in a constrained JavaScript engine without CLR, arbitrary file reads, processes or network access. Execution has memory, statement, time and cancellation limits. Explicit export is the exposed file-writing operation. AI calls require compatible connected ComfyUI and an Engine Pack. Local edits and queued AI tasks commit together as one history entry. A script generated for another document tab is not applied to the current tab.

See [Scripting](scripting.md) for API examples.
