# Assistant chat

Open **Assistant** in the tool-options row or **AI > Assistant**. Type a question or describe an edit and press **Send** (Enter; Shift+Enter adds a line). The conversation survives closing/reopening the window during the same application session. **New chat** clears it; conversations are not written to disk.

The assistant can answer without changing the document. Requested edits are applied as one Undo step by default. Disable **Apply requested edits** to review the generated script and choose **Apply edit** yourself. Editing responses expose an expandable script, **Save script** and **Copy**. **Stop** cancels generation/execution; failed document changes roll back.

## Attachments and context

Use **Attach** or drag files into the chat. Up to six files can accompany a message; × removes unwanted attachments. Supported files include `.js`, `.ts`, `.txt`, `.md`, `.json`, `.csv`, `.yaml`, `.yml`, `.svg`, PNG, JPEG, WebP and BMP. Text files are limited to 256 KB. A script is sent as data, not executed simply because it was attached: explicitly ask to explain, adapt or run it.

`doc.addAttachedImage(index)` imports an explicitly attached image. The index is zero-based across all attachments in the current message. No arbitrary filesystem-reading API is exposed.

Each request includes current document geometry, layer hierarchy, names, types, tags, text and transforms, plus the current scripting contract. Document context is bounded to 160 layers and 500 characters per text layer. Chat context includes up to 20 recent messages within 24,000 characters; individual messages are bounded to 8,000 and attachment text to 12,000 characters. Local requests reduce these limits to fit the configured context, reserving space for output and prioritizing current files over older messages. Truncation is marked. Text attachment context remains in subsequent turns; reattach images to import them in a later turn.

Enable **Send document preview and attached images** only for a model that supports vision. Images sent to the model have a maximum side of 1024 pixels; importing uses the original file. Without vision the assistant can import images but cannot inspect their pixels.

## Providers

The ⚙ menu selects the provider:

- **Local llama.cpp** uses a GGUF model and the configured `llama-server`. Automatic startup is limited to loopback URLs. The Windows launcher discovers the supplied executable and model relative to the project; paths, context size and reply limit are configurable.
- **API (chat completions)** accepts an HTTP/HTTPS base URL or complete `/chat/completions` endpoint, a model ID and optional Bearer key. A root URL gets `/v1/chat/completions`; custom base paths are preserved. No local health/startup contract is required. This supports chat-completions-compatible APIs, not every vendor's distinct protocol.

An entered key remains only for the current application session and is never serialized. For a persistent credential, name an environment variable (default `COMPOSA_ASSISTANT_API_KEY`). JSON response format is opt-in for remote providers; ordinary text replies and fenced JavaScript fallbacks are also accepted.

Remote providers receive document context, chat and attachment text. Images are sent only when vision is enabled. Model weights are never uploaded.

## Execution safety

Scripts use existing editor commands in a constrained JavaScript engine without CLR, arbitrary file reads, processes or network access. Execution has memory, statement, time and cancellation limits. Explicit export is the exposed file-writing operation. AI calls require compatible connected ComfyUI and an Engine Pack. Local edits and queued AI tasks commit together as one history entry. A script generated for another document tab is not applied to the current tab.

See [Scripting](scripting.md) for API examples.
