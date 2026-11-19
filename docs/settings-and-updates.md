# Settings and updates

## What Composa remembers

Between launches, Composa keeps: the window size and whether it was maximized, the recent files, the JPEG export quality, the view options (rulers, grid, guides, snapping and what to snap to, locked guides, the pixel grid and the transform controls), Auto Select, whether the History panel is shown or collapsed and how tall it is, your changed keyboard shortcuts, whether AI control is allowed, and the update check settings. Tool settings and colors carry from tab to tab within a session but start fresh at the next launch.

## Where files live

| | Linux | Windows |
|---|---|---|
| Settings | `~/.config/composa` | `%APPDATA%\Composa` |
| Recovery copies | `~/.cache/composa/recovery` | `%LOCALAPPDATA%\Composa\recovery` |

On Linux the XDG environment variables are honoured if you set them. The pipe an AI agent connects through lives in the same cache folder; see [AI control](ai-control.md).

## Updates

Help > Check for Updates checks the release feed. Windows Studio 12 and later installations offer **Download changes**, followed by **Restart to update**. Keep the extracted portable folder together, including its `packages` directory and, from Studio 13, `portable-baseline.json`. Studio 13 builds its initial baseline locally when you first download changes. The editor uses that local baseline to reconstruct the new version from verified patches. If a patch is unavailable, the editor offers a retry and a link to the release. Studio 11 and earlier require one manual upgrade to an update-enabled installation.

Help > Check for Updates Automatically checks once a day at launch. You can dismiss the notice or skip a particular version. Stable installations follow stable releases, and Studio previews can receive newer previews.

A Composa installed from a `.deb` or `.rpm` package is updated by your package manager, and the Help menu says so instead of offering to check. To stop the automatic check on any build, set the environment variable `COMPOSA_DISABLE_UPDATE_CHECK` to 1.

## If something goes wrong

An unexpected error shows a "Something went wrong" dialog with the error and advice to save a copy with File > Save As. The document stays open. A crash leaves recovery copies, which the next launch offers to restore.
