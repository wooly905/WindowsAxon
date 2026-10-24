# WindowsAxon

WindowsAxon is a Windows 11 x64 application that accepts plain-text requests and runs them through an agent. The desktop interface uses WinUI 3 and CommunityToolkit.Mvvm.

The desktop app uses a real model endpoint and 88 operational tools for Windows, files, registry, printers, event logs, personalization, devices, network, applications, utilities, browser automation, desktop controls, Paint, scripts, and Microsoft Learn. Tools are enabled by group as needed. The desktop app does not use evaluation fake tools.

Applications are indexed from Windows AppsFolder. Packaged apps such as Paint are activated through Windows, and a visible process window is verified before reporting that the app opened. For other app types, the tool reports a launch request without claiming its window was verified. Closing a window uses the application's normal close flow, preserves save prompts, and reports success only after verifying that the window closed. Launching with files or command-line arguments is not supported.

Successfully launched applications run independently. Ending a tool call, starting a new conversation, or closing WindowsAxon does not close those applications. Closing an application or browser tab requires an explicit close action.

Windows management consoles such as Disk Management use `admin_tool_open` in the `settings` group. MMC snap-ins are launched through `mmc.exe`, and the tool reports an open console only after verifying a visible window. An unverified launch request may be waiting for Windows or a UAC prompt. Scripts run in an isolated process job. Successful completion preserves child processes; Stop, timeout, or failure terminates the job. After successful completion, those child processes run independently and are no longer controlled by the script's Stop action. A script exit code of 0 confirms script completion, not an open application.

## Run the desktop app

Install the .NET SDK selected by `global.json` and start an OpenAI-compatible model service. The default endpoint is Ollama at `http://localhost:11434/v1`, using model `windowsaxon-qwen3.5-9b`.

For Ollama, create the default model alias with a 16,384-token context window:

```powershell
ollama pull qwen3.5:9b

@'
FROM qwen3.5:9b
PARAMETER num_ctx 16384
'@ | Set-Content "$env:TEMP\WindowsAxon.Modelfile" -Encoding utf8

ollama create windowsaxon-qwen3.5-9b -f "$env:TEMP\WindowsAxon.Modelfile"
```

From the repository root:

```powershell
dotnet run --project src/WindowsAxon.App
```

The app is unpackaged and includes its Windows App SDK runtime, so launching it does not require MSIX registration. In Visual Studio, select `WindowsAxon.App` as the startup project.

- Enter an English request, then click **Send** or press **Enter**. **Shift+Enter** inserts a new line.
- **Stop** cancels the active request, including a pending approval or step-limit card.
- **Ctrl+Alt+Q** stops mouse and keyboard input. Input also stops when another window becomes active.
- Reply to a clarification in the input box, or select **Start a new request instead**.
- **New chat** creates a new conversation and clears conversation-level approvals.
- **Settings** changes the endpoint URL, model name, and optional API key, then starts a new conversation. Leave the key field blank to keep the configured key for the same endpoint, enter a replacement, or select **Remove saved API key**. Changing the endpoint requires entering its key again.

WindowsAxon replies display Markdown headings, bold and italic text, strikethrough, lists, task lists, quotes, tables, links, and code blocks. Text remains selectable, and wide tables can scroll horizontally. User messages and errors remain plain text. HTML is displayed as text; images appear as labels and links without automatic downloads. Mermaid code blocks currently display their source rather than a diagram.

Settings are saved to `%LOCALAPPDATA%\WindowsAxon\settings.json`. On startup, the app reads this file if it exists; otherwise it reads `settings.local.json` in the current working directory, or uses defaults. An explicit configuration file can also be supplied:

```powershell
dotnet run --project src/WindowsAxon.App -- --config C:\path\settings.json
```

The JSON format is the `Models` configuration in the implementation plan. The Settings dialog stores API keys in Windows Credential Manager and writes only a `credential:<name>` reference to JSON. Remote endpoints with keys require HTTPS; loopback endpoints may use HTTP. Manually configured credential references are also supported; removing or replacing them in Settings detaches the reference without deleting the externally managed credential. Requests and tool results go to the configured endpoint; a remote endpoint receives them outside this computer.

When saving a remote endpoint, Settings omits the local `reasoning_effort: none` default so the remote model can use its own default. Other configured reasoning effort values are preserved. For routers that choose a model, enter `auto` in **Model name**. Remote `auto` uses the Responses API with native tool calling, so its endpoint must support `/responses`. Requests use `store: false` and send full conversation history, allowing the router to select a backend for each request. The `auto` alias leaves temperature to the router's selected model. Other model names continue to use Chat Completions.

Audit logs are stored locally in `%LOCALAPPDATA%\WindowsAxon\logs\audit-YYYYMMDD.jsonl`, with one JSON object per line. Logging is enabled automatically. Every invoked tool, including `enable_tools`, `load_skill`, and `read_skill_resource`, writes a `call_start` entry followed by a `call` entry sharing the same `callId`. Entries include request text, full tool arguments, risk, approval decision, status, elapsed milliseconds (`durationMs`), full returned content (`result`), and exception details with stack traces (`error`). Results are logged before WindowsAxon shortens observations for the model; any limits applied by the tool itself still apply. Blocked, failed, timed-out, and cancelled invocations are recorded. Approval decisions use `kind: "approval"`; denied actions are recorded there without being executed. Payment card numbers are masked in requests, arguments, results, and errors. Other content remains in the local log, and logs are not automatically deleted.

If the model returns an empty answer, WindowsAxon asks it to continue the same request once, preserving tool results and active groups. A second empty answer is reported as an error. Each empty answer writes an audit entry with `kind: "response"` and `status: "empty"`.

In Native mode, an endpoint may return only reasoning text with no answer or tool call. WindowsAxon detects this incomplete response and switches that conversation to Structured steps. The switch writes a `kind: "model_fallback"`, `status: "structured"` audit entry with the model, finish reason, and reasoning text. Settings remain unchanged; a new conversation starts in the configured mode.

To watch today's tool execution log in PowerShell:

```powershell
Get-Content "$env:LOCALAPPDATA\WindowsAxon\logs\audit-$(Get-Date -Format yyyyMMdd).jsonl" -Tail 20 -Wait
```

## Updates

Thirty seconds after each launch, WindowsAxon asks the GitHub API for the latest release of `wooly905/WindowsAxon`. No other data is sent. If the release is newer than the running version, a bar at the top of the window offers two choices:

- **Update** downloads the `WindowsAxon-<version>-win-x64.exe` release asset and verifies it against its `.sha256` file. It then replaces the running executable, keeping the file name, and restarts the app. The update button is disabled while a request is running. If a request starts during the download, installation waits until it finishes. If the file cannot be replaced, for example because the folder is not writable, or the new version fails to start, the original executable is restored and the release page opens for a manual download. Copies run from build output (`dotnet run`) also open the release page instead of replacing themselves.
- **Skip** hides the bar and stops offering that version. A newer release is offered again. The skipped version is stored in `%LOCALAPPDATA%\WindowsAxon\update.json`.

Nothing is installed without selecting **Update**. If the network is unavailable or the GitHub API cannot be read, no message is shown.

## Tool catalog

The operational catalog has 88 tools. `load_skill`, `read_skill_resource`, and `enable_tools` provide skill and group selection.

| Group | Tools |
|---|---|
| apps | `app_list`, `winget_search`, `winget_install` |
| base | `app_find`, `app_launch`, `app_refresh_index`, `ui_list_windows`, `ui_close_window`, `docs_search` |
| browser | `browser_open`, `browser_snapshot`, `browser_find`, `browser_read`, `browser_click`, `browser_type`, `browser_press`, `browser_scroll`, `browser_back`, `browser_tabs`, `browser_tab_select`, `browser_close` |
| desktop_ui | `ui_focus_window`, `ui_snapshot`, `ui_click`, `ui_type`, `ui_press_keys`, `ui_drag`, `ui_read_text`, `paint_new_canvas`, `paint_draw_shape`, `paint_draw_free`, `paint_add_text`, `paint_save`, `ui_screenshot` |
| devices | `volume_get`, `volume_set`, `brightness_set`, `power_plan_list`, `power_plan_set` |
| docs | `docs_code_search`, `docs_fetch` |
| eventlog | `eventlog_list_logs`, `eventlog_query` |
| files | `fs_list_dir`, `fs_find_files`, `fs_read_text`, `fs_copy`, `fs_move`, `fs_create_dir`, `fs_delete`, `acl_get`, `acl_set` |
| network | `net_get_config`, `net_ping`, `net_dns_lookup`, `net_test_port` |
| personalize | `wallpaper_get`, `wallpaper_set`, `theme_get`, `theme_set_mode`, `theme_list`, `theme_apply` |
| printers | `printer_list`, `print_queue_list`, `print_job_cancel`, `printer_print_test_page`, `printer_set_default` |
| registry | `registry_read`, `registry_set_value`, `registry_delete_value` |
| scripts | `script_create`, `script_run`, `python_ensure`, `python_install_package` |
| settings | `settings_open`, `control_panel_open`, `admin_tool_open` |
| system | `system_get_info`, `system_get_performance`, `disk_get_usage`, `process_list`, `process_stop`, `service_list` |
| utilities | `clipboard_get`, `clipboard_set`, `calc_evaluate`, `undo_list`, `undo_last` |

## Tool behavior

- Windows changes require approval. Browser clicks, desktop actions, script execution, and installations request approval for each action. Sending file, browser, or desktop text, including snapshot names and titles, to a remote model also requires approval.
- Registry writes affect HKCU only and cannot change policy keys. File deletion uses the Recycle Bin. Credential files, password fields, payment fields, security windows, and password-manager windows are blocked by dedicated file and UI tools.
- Registry, permission, wallpaper, theme, audio, brightness, power-plan, and default-printer changes can be restored with `undo_list` and `undo_last`. The journal keeps seven days of entries and detects subsequent changes. Scripts, installs, file moves, and deletion have no automatic undo.
- Browser tools independently launch installed Microsoft Edge or Google Chrome and connect through Playwright's Chrome DevTools Protocol support, with a separate visible profile under `%LOCALAPPDATA%\WindowsAxon\browser-profile`. They do not control the user's regular browser profile. Stop, a new conversation, and closing WindowsAxon disconnect automation while preserving browser windows and tabs. Later browser requests reconnect to the same profile. `browser_close` explicitly closes the selected tab. Sign-in, CAPTCHA, and protected input require the user.
- `browser_read` returns the full visible page text, or the full visible text of a referenced element, to the model and audit log. It excludes HTML markup, scripts, styles, templates, comments, and hidden content while preserving text line breaks. It bypasses the usual 1,500-character observation limit so content after the navigation and page header remains available. Scrolling does not change the starting point of a full-page read.
- `ui_screenshot` captures one window as a PNG, scaled to at most 1,280 pixels on the longer side, and sends it to the model as an image. The model must accept images; if the endpoint rejects a request containing an image, WindowsAxon resends it once without the image and tells the model to use `ui_snapshot` or `ui_read_text`. When that retry succeeds, images are no longer sent for the rest of the conversation, and a `kind: "model_fallback"`, `status: "no_images"` audit entry is written. Protected windows are blocked as with other desktop tools, minimized windows are refused, and a remote endpoint requires approval. Only the newest screenshot is sent with each model call; the audit log records the text description, not the image.
- Paint tools create canvases, draw shapes and freehand paths, add text, and save images through Paint's interface. Drawing requires the entire canvas to be visible. The adapter currently uses English control names where stable automation IDs are unavailable; other versions or languages may return an unsupported-control error. Saved files are checked for existence and image format.
- PowerShell and Python scripts are generated in `%LOCALAPPDATA%\WindowsAxon\work\<session>`, checked for syntax, and shown in full before execution. Modified scripts are refused. Execution uses the current user's permissions, a 120-second timeout, a 1 GB Windows Job Object limit, and output capture capped at 1 MB per stream. This is process isolation, not a sandbox restricting file or network access. Review the code before allowing it. Administratively configured PowerShell execution policies prevent PowerShell tools from running.
- `python_ensure` creates WindowsAxon's virtual environment and may install Python through winget after approval. `python_install_package` installs only into that environment. `winget_install` requests a current-user install; packages without that installation scope return an error.
- `docs_search`, `docs_fetch`, and `docs_code_search` connect to Microsoft Learn MCP. The first call in each conversation requests approval to contact Microsoft Learn.
- Brightness requires a supported built-in display. Printer actions require installed printers. Tools return errors when required hardware, permissions, or command-line programs are unavailable.

The UI provides an execution indicator, approval cards, and final results. Streaming steps, the first-run wizard, and the full Test connection checks remain later milestone work. The evaluation CLI continues to use simulated tools.

## Build and test

Build and run tests on Windows with the SDK selected by `global.json`. The `WindowsAxon.Windows` project uses Windows desktop assemblies for UI Automation and Clipboard; the application interface remains WinUI 3.

Browser regression tests use installed Microsoft Edge in headless mode with isolated contexts. No browser download is required.

```powershell
dotnet build WindowsAxon.slnx
dotnet test --solution WindowsAxon.slnx
```

The evaluation CLI remains available:

```powershell
dotnet run --project src/WindowsAxon.Eval -- --config settings.local.json
```
