# Local Assistant

Composa includes a local editing Assistant. Open it with the compact **Assistant** button in the tool-options row or **AI > Assistant**.

Type a task, choose **Plan**, and review both the explanation and generated JavaScript before choosing **Apply**. Nothing is applied while the plan is being generated. **Apply** runs the reviewed script and any queued AI tasks as one Undo step. **Save as Script** keeps useful automation as a normal `.js` file.

The first provider uses `llama-server` from llama.cpp and a local GGUF model. It sends the document title, dimensions, selection state, layer paths, names, types, tags, text and transforms, together with the current scripting contract. Context is bounded to 160 layers and 500 characters per text layer, and reports when it was truncated. It does not send model weights or API keys. The bundled Windows launcher discovers the supplied llama.cpp and Qwen model through paths relative to the project; use **Settings** in the Assistant window to choose different files, URL, context size, or reply limit. Automatic process startup is limited to loopback server URLs.

Layer tags make automation deterministic: the Assistant looks for a relevant tag such as `title` before falling back to layer name and type. The plan remains editable, so an ambiguous result can be corrected without asking the model again.

The provider contract is separate from the UI and script runtime. Other OpenAI-compatible or hosted providers can be added later without changing document commands. The local provider currently supplies text/document structure rather than a rendered visual preview; tasks that genuinely need visual interpretation should use the available ComfyUI AI tasks.

## Safety and limits

- Scripts run in a constrained JavaScript engine with no CLR or arbitrary filesystem API. Execution has time, memory, statement and cancellation limits.
- Export is the only exposed file-writing operation, and the Assistant is instructed to emit it only when explicitly requested.
- The script is always shown before Apply.
- ComfyUI must be connected and have a compatible Engine Pack for `ai.*` calls.
- Local editor changes and all queued `ai.*` calls roll back together on failure and commit as one history entry on success.

See [Scripting](scripting.md) for the API and examples.
