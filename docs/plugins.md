# Scripts and plugins

The built-in library is available in **Scripts > Scripts & Plugins** or **Plugins > Manage / Install Plugins**. Saved scripts appear in Scripts; enabled package commands appear in Plugins. Both can receive shortcuts through **Help > Keyboard Shortcuts**. A package is standard JavaScript plus a `plugin.json` manifest, not a native DLL or a Photoshop extension.

## Install and manage

Choose **Install Plugin** and select `plugin.json` from an unpacked package folder. Review the author/source, command count and requested permissions before confirming. Composa validates the manifest and copies only the declared scripts and optional README/license into its config folder. Loading, scanning and enabling a plugin never executes its code. Choose a command to run it against the active document, as one Undo step.

Disable a plugin without losing installed files or shortcuts. **Remove** moves the package into `automation/archive` instead of deleting it. Reinstall the archived `plugin.json` to restore it. To upgrade, remove the old package and install the new one; existing packages are not silently overwritten. Stable plugin/command IDs preserve shortcuts. Scripts can also be removed recoverably from the same manager.

**Edit** opens a library script. **View / Copy** opens a plugin command's code in the script editor; saving it makes a separate library script rather than altering the installed package. The previous version of an edited library script is kept as `.bak`.

## Package contract (API version 1)

```json
{
  "id": "basic-editing",
  "name": "Basic Editing",
  "version": "1.0.0",
  "apiVersion": 1,
  "permissions": [],
  "commands": [
    { "id": "blue-card", "title": "Blue Card", "script": "blue-card.js" }
  ]
}
```

IDs use lowercase letters, numbers and hyphens, at most 64 characters. A package contains 1–32 uniquely identified commands. Script paths are relative forward-slash `.js` paths inside the package. Absolute paths, traversal and linked files/folders are rejected. Each script is at most 256 KB and the total declared script size is at most 2 MB. Unknown API versions or permissions fail validation and appear in the manager's diagnostics without preventing ordinary editor startup.

No permissions means editing the active document and queuing normal Composa AI tasks only. The optional `"export"` permission enables `doc.export(path)` and is displayed before installation; it permits filesystem writes, so approve it only for trusted code. No CLR, native modules, process launching, arbitrary file reads, network calls or automatic startup hooks are exposed. External provider access happens through the existing AI service, not custom plugin network code. Scripts are sandboxed and bounded as described in [Scripting](scripting.md).

A working package is included in [`examples/plugins/basic-editing`](../examples/plugins/basic-editing). Build and packaged output also includes `examples/`. Install its manifest to try live shapes and text, or use its scripts individually. These commands need neither ComfyUI nor an Assistant model.
