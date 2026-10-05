# Background Computer Use for Windows (BgDesk)

Background computer use for coding agents on Windows: the agent works on a hidden desktop of its own while you keep using your PC.

Coding agents that drive a GUI on Windows take over the screen you are working on: the pointer moves, focus jumps, and touching the machine breaks the run. BgDesk gives the agent a Windows desktop that you never see. The agent clicks, types and drags there with real input while your monitors, pointer and keyboard stay yours.

Several agents can work at the same time through the same MCP server. The background desktop is one large screen divided into areas that do not overlap, and each agent gets one: its own 1440x900 screen with its own windows.

It is an MCP server for Codex, Claude Code, Cursor and other MCP clients. It is meant for apps that can only be operated through their GUI. If something has a CLI, an API or its own MCP server, use that instead.

## Install

Each line downloads the latest release to `%LOCALAPPDATA%\Programs\BgDesk`, puts it on your PATH and registers the MCP server in that agent. Run it in PowerShell.

| Agent | Command |
| --- | --- |
| Codex | `$env:BGDESK_AGENT='codex'; irm https://raw.githubusercontent.com/enwong93-sketch/background-computer-use/main/install.ps1 \| iex` |
| Claude Code | `$env:BGDESK_AGENT='claude-code'; irm https://raw.githubusercontent.com/enwong93-sketch/background-computer-use/main/install.ps1 \| iex` |
| Claude Desktop | `$env:BGDESK_AGENT='claude-desktop'; irm https://raw.githubusercontent.com/enwong93-sketch/background-computer-use/main/install.ps1 \| iex` |
| Cursor | `$env:BGDESK_AGENT='cursor'; irm https://raw.githubusercontent.com/enwong93-sketch/background-computer-use/main/install.ps1 \| iex` |
| Windsurf / Devin Desktop | `$env:BGDESK_AGENT='windsurf'; irm https://raw.githubusercontent.com/enwong93-sketch/background-computer-use/main/install.ps1 \| iex` |
| VS Code (GitHub Copilot) | `$env:BGDESK_AGENT='vscode'; irm https://raw.githubusercontent.com/enwong93-sketch/background-computer-use/main/install.ps1 \| iex` |
| Gemini CLI | `$env:BGDESK_AGENT='gemini'; irm https://raw.githubusercontent.com/enwong93-sketch/background-computer-use/main/install.ps1 \| iex` |
| OpenCode | `$env:BGDESK_AGENT='opencode'; irm https://raw.githubusercontent.com/enwong93-sketch/background-computer-use/main/install.ps1 \| iex` |
| Every agent found on this PC | `$env:BGDESK_AGENT='all'; irm https://raw.githubusercontent.com/enwong93-sketch/background-computer-use/main/install.ps1 \| iex` |

Already installed? Add another agent with `bgdesk install <agent>` and remove one with `bgdesk uninstall <agent>`. The first time, the agent's config file is copied to `<name>.bak-bgdesk` beside it.

Then, once, from an elevated terminal:

```powershell
bgdesk enable
```

This calls `WTSEnableChildSessions(TRUE)`. No reboot. `bgdesk disable` reverts it.

Any other MCP client: point it at `bgdesk.exe` with the argument `mcp`.

Codex has been registered and driven end to end, and Claude Code registered. For the other agents the installer writes the config file and format their documentation gives; they have not been run by us.

### Requirements

- Windows 10 or 11, x64 (child sessions exist since Windows 8; developed on Windows 11 Enterprise 25H2). Nothing else to install: the release includes its own .NET runtime.
- A Windows account **with a password**. Windows refuses Remote Desktop logons for blank-password accounts, and a PIN does not count.
- Administrator rights once, for `bgdesk enable`.

The first start shows the standard Windows credential prompt. After a successful logon the password is kept in Windows Credential Manager as `BgDesk/child-session` and later starts are silent.

### Make the agent reach for it

The agent decides which tool to use. Without guidance it may pick a foreground computer-use tool, so give it the skill in [`skills/bgdesk`](skills/bgdesk/SKILL.md). The skill is loaded only when a task needs it.

Codex:

```powershell
$d = "$HOME\.codex\skills\bgdesk"; New-Item -ItemType Directory -Force $d | Out-Null; irm https://raw.githubusercontent.com/enwong93-sketch/background-computer-use/main/skills/bgdesk/SKILL.md -OutFile "$d\SKILL.md"
```

Claude Code:

```powershell
$d = "$HOME\.claude\skills\bgdesk"; New-Item -ItemType Directory -Force $d | Out-Null; irm https://raw.githubusercontent.com/enwong93-sketch/background-computer-use/main/skills/bgdesk/SKILL.md -OutFile "$d\SKILL.md"
```

Optional: for an agent that should always prefer the background desktop, add the one sentence in [`skills/AGENTS-snippet.md`](skills/AGENTS-snippet.md) to its global instruction file (`~/.codex/AGENTS.md`, `~/.claude/CLAUDE.md`). That file is sent with every request, which is why the snippet is a single sentence and the detail lives in the skill.

## How it works

BgDesk uses a Windows **child session**, a documented loopback Remote Desktop session that runs under your existing logon ([Microsoft docs](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions)). It is the same account with the same files and installed apps, on a second desktop that has its own input queue. It is an ordinary desktop, so `SendInput`, screen capture and drag and drop between windows behave as they do for a person.

| Part | Runs in | Job |
| --- | --- | --- |
| `bgdeskw host` | your session | Hosts the Remote Desktop control off-screen and keeps the child session alive. Tray icon to look at the background session or stop it. Independent of the MCP client: if it stops, the next tool call reconnects to the same session with its apps still open. |
| `bgdeskw agent` | child session | Captures the screen and sends real mouse and keyboard input. Listens on a current-user-only named pipe. |
| `bgdesk mcp` | wherever the MCP client starts it | stdio MCP server. Starts the desktop on the first tool call. |

## Layers

The background desktop is one large screen (6360x3228) divided into twelve areas of 1440x900, in four columns and three rows, with a gap of 120 pixels around each and a strip of 48 pixels reserved at the bottom of the screen for the taskbar. Every agent that connects gets one of them, its layer, the first time it uses a tool. For the agent the layer is the whole screen: pictures show only it, coordinates start at its top-left corner, and nothing outside it can be clicked.

- Nothing to configure. Each MCP server process is one agent and one layer.
- The windows of a layer stay inside it. A window that a program opens elsewhere is moved in, one that is too large is made to fit, a maximized window fills the layer, and a minimized one is restored (a layer has no taskbar to bring it back from). A window belongs to the layer whose agent started its program; one made by another process for that start (a folder window, a Store app, a copy of the program that was already running) is claimed by the starting layer.
- All layers are on screen at the same time, so a picture is always taken straight from the screen and shows exactly what is there.
- A layer is removed, and everything started on it is closed, when its agent disconnects or calls `desktop_stop`. This also happens if the agent is killed.
- Each layer has its own clipboard: text, rich text, HTML, copied files and images.
- Up to 12 layers at a time.

## Working at the same time

The session has one pointer and one keyboard. An action that needs them takes a turn; much of what an agent does does not, and runs at the same moment as other agents' work.

The agent does not choose and the tools do not change. Each call is routed by what it is and what it is aimed at:

| Done without a turn, in parallel | Takes a turn with real input |
| --- | --- |
| `screenshot`, `list_windows`, `launch`, `clipboard_read`, `clipboard_write` | `drag` |
| A left `click`, double or triple click, with or without Ctrl and Shift, in a Chromium browser, an Electron app, File Explorer, a console or a classic dialog | Right and middle clicks, clicks with Alt; clicks in any other kind of program; clicks on menus, drop-down lists and title bars |
| `key` in those programs: letters, digits, Enter, Tab, Esc, arrows, Home, End, Page Up and Down, Backspace, Delete, F-keys, with Ctrl and Shift, and Alt+F4, Alt+arrows and Alt+Home | Shortcuts that use the clipboard (Ctrl+C, Ctrl+X, Ctrl+V, Ctrl+Insert, Shift+Insert, Shift+Delete), Ctrl+Pause, the Windows key, other Alt shortcuts and F10 (they open menus), Ctrl+C in a console; keys anywhere else |
| `type` of text into a Chromium browser, an Electron app, a console, or a text box in File Explorer or a classic dialog (a line break is sent as Enter, a tab as Tab) | typing anywhere else, including into a file list |
| `mouse_move` over a Chromium page or Electron app | `mouse_move` anywhere else |
| `scroll` in a Chromium browser, an Electron app, File Explorer or a classic dialog | `scroll` anywhere else, including a console, which a wheel message moves too little |

The parallel ones send messages to the window directly instead of using the pointer and keyboard. The left column is the list of what was tested to behave exactly like real input. Anything not on it, and anything in doubt, uses real input, which works everywhere. A layer that already has the keyboard is always used with real input.

The two ways are kept in step. A click made without a turn leaves its window active, so key presses that follow reach the same place. If a dialog or another window has come up since, typing is sent with real input so that it goes where a person's typing would. A click that opens a list or menu is continued with real input.

An action done without a turn is followed by a look at the window. If nothing on it changed, the result says so (`ok, but nothing visibly changed`), since some control may have ignored the message. The agent then takes a screenshot and, if the control really ignored it, repeats the call with `input=real`. `click`, `mouse_move`, `scroll`, `key` and `type` accept `input=real` to force real input; the default is `auto`.

When an action does need a turn:

- Turns are given in the order they were asked for.
- The keyboard is moved to the layer's window. While a turn is on, and between turns, the foreground is locked, so a window that another agent's program opens cannot take the keyboard away in the middle of typing; before each key press the tool also checks that the keyboard is still in the layer.
- A layer that has a menu, a drop-down list or a drag open keeps the keyboard until it is gone, for at most 20 seconds, so another agent's turn cannot close it. Others wait. If it stays open longer, the next turn of another agent closes it. The agent that had it is told so with its next action. When that action would now land behind the closed menu (a click where the menu was, or Enter, Space, Tab or an arrow key) it is not done; anything else is done as usual.
- Each layer's pointer is put back where that layer left it.
- Held keys are shared by the whole session: a program asks Windows whether Ctrl is down when it handles a mouse wheel or a click. Real input therefore runs alone, and actions without a turn wait the few milliseconds it takes.

A program started with `launch` is brought to the top of its layer. `launch` of a folder or a document waits for its window, so the window cannot appear later over something started after it. Console programs are started in the classic console host, since the default terminal would put every console into one shared window.

Shortcuts that act on the whole screen rather than on a window are refused with a message: Win key combinations other than Win+R and Win+E (Win+D, Win+M, Win+Tab, Win+arrows, Win+L and so on), Alt+Tab, Alt+Esc, Ctrl+Esc and Ctrl+Alt+Del. They would reach other agents' layers.

## Tools

| Tool | What it does |
| --- | --- |
| `screenshot` | PNG of the agent's layer, 1440x900. Coordinates for the tools below are pixels of this image. |
| `click` | Left, right or middle click; single, double or triple; optional held modifiers. `click`, `mouse_move`, `scroll`, `key` and `type` take `input=real` to force real input. |
| `mouse_move` | Hover. |
| `drag` | Press, move through waypoints, release. Works between application windows. |
| `scroll` | Wheel up, down, left or right. |
| `key` | Chords such as `ctrl+s`, `alt+f4`, `win+r`. Shortcuts that act on the whole screen are refused. |
| `type` | Unicode text. |
| `launch` | Start a program, document, folder or web address in the agent's layer. Says so when the program opened no window. |
| `list_windows` | Visible windows in the agent's layer with rectangles. |
| `clipboard_read`, `clipboard_write` | Text on the layer's clipboard, which is separate from yours and from other layers'. |
| `desktop_stop` | Close the agent's layer and everything running on it. |

## CLI

```
bgdesk mcp | install <agent> | uninstall <agent>
bgdesk up [width height] | down | status | shot <file.png> | call <op> [json] | enable | disable
```

The background desktop is 6360x3228 unless `up` is given another size; a smaller desktop is divided into as many layers as fit, or used whole as one layer. `bgdesk down` logs the whole background session off, with every layer; `shot` and `call` use a layer named `main` unless the JSON names another with `"layer"`.

## Build from source

```powershell
dotnet build src\bgdeskw\bgdeskw.csproj -c Release
dotnet build src\bgdesk\bgdesk.csproj -c Release
```

Building needs the .NET 10 SDK. Both land in `dist\`. A running agent keeps `bgdeskw.exe` in use: stop the desktop (`bgdesk down`) first, or rename the files in use, build, and restart the agent.

## What has been tested

On one machine (Windows 11 Enterprise 25H2, build 26200), on the build unpacked from the release package (regression, fusion, canvas tiles, key, mouse and hard-page suites and a stress run) and, for the rest, on the development build of the same source, from a cold start of the background session:

- Screenshot, typing including CJK text, keyboard shortcuts.
- Three agents on separate MCP connections typing at the same moment, each seeing only its own window, each with its own clipboard; a copied file surviving another layer's turn.
- Dragging a file from a folder window onto Chrome inside a layer.
- A killed agent's layer being removed with the apps on it; twelve layers at once, a thirteenth refused.
- The host being stopped and the session logged off while agents were connected; the next tool call recovered in both cases. Starting right after stopping, three times in a row. A session whose agent had not started (left by an earlier version) was logged off and started again by the next call, after 45 seconds.
- Store apps: Notepad started from the second layer opened in that layer; Calculator in the first.
- In one layer: a window maximized with its maximize button filled the layer and nothing more; a minimized window came back; a Save As dialog opened inside the layer; Win+R opened the Run box inside it; Win+D and Alt+Tab were refused with a message, and a point outside the layer was refused. The window in the other layer did not move.
- Three agents each doing a random mix of what agents do (page clicks and typing, scrolling, hovering, shortcuts, right and double clicks, starting consoles and folder windows, opening the page again in a new window and typing into it) for four minutes, with two different random sequences (255 operations each on the development build, 240 more in a run on the unpacked package): no errors, no typing lost, no input reaching the wrong layer, every layer given back. Measured on its own, a call took 0.3 seconds at the median and 2.5 seconds at the 95th percentile; about one call in thirty waited 10 seconds or more, each of them real input waiting while another agent had a menu open (see above).
- The same mixed test against the earlier design (one desktop per agent, taking turns at the screen), three runs each: the tiles did 1.6 to 3.7 times as many operations in the same time, with as few errors (none).
- Three agents working without pause for three minutes: 1,536 operations, no errors, no input reaching the wrong layer.
- A picture of a layer against another taken right after: the same, apart from the pointer and what really changed (an inactive title bar turning active).
- Clicks without a turn on a page with a canvas, a range slider, a hover area, a checkbox, elements that listen only for mouse down or only for pointer down, a link, a text area and an editable region: all of them took effect while another layer had the keyboard, and a click on a canvas landed within one pixel of where it was aimed.
- One layer holding a menu open while another clicked, typed, scrolled and took a picture in a web page: six actions in about 3 seconds, the menu stayed open.
- Programs started by one agent's `launch` while another was typing: without the foreground lock all of Chrome, Character Map, a folder window, a console and Notepad took the keyboard; with it none did.
- Clicking page content in Chrome and clicking in VS Code (Electron); VS Code and a web address open in two layers at once, each in its own copy.
- Blender in a layer: it opened filling the layer, the 3D viewport rendered, the splash screen closed on a click, a middle-button drag orbited the view and the File menu opened; the window in the other layer did not move.
- A menu held open past 20 seconds while another agent waited: the other agent got its turn after 20 seconds; the first agent's next click where the menu had been, or a Down arrow, was not done and said why; a click elsewhere or Esc was done and carried the note.
- `codex exec` calling the tools through MCP.
- The user's pointer position and foreground window were unchanged across these runs.
- Cold start to first screenshot: about 8 to 10 seconds. Reconnecting after the host stopped: about 3 seconds.

Not tested: game engines, sessions lasting hours, other Windows editions and builds, and the agents other than Codex and Claude Code.

## Limitations

- **Programs that allow only one running copy.** A second start of such a program passes its work to the copy already running and exits, and the work then happens in that copy's window, which stays in the layer that has it. Chromium browsers and Electron apps (VS Code, Cursor and the like) are given a data folder per layer, so each layer gets its own copy; that copy does not have your logins or settings. Web addresses open in such a browser in the layer rather than in the browser you have open. For other single-copy programs only one layer can have the program, and `launch` tells the agent when its start was passed on.
- **Shortcuts that act on the whole screen are refused** (see above), and there is no taskbar or Start menu in a layer. Agents use the window's own controls.
- **A window is kept in its layer by moving it.** A program that insists on its own place or size is moved back up to 20 times in ten seconds and then left where it is.
- **Clipboard formats other than text, rich text, HTML, files and images** are not carried over when layers take turns.
- **An action without a turn can be ignored by a control that only reacts to real input.** Nothing tells the tool that when it happens, so it looks at the window afterwards and says so when nothing changed. A click on an empty area gives the same answer, so the agent decides, and can repeat with `input=real`. A slow reaction, such as a page that takes a moment to load, can also be reported as no change.
- **Elevated windows cannot be driven.** The agent runs unelevated and Windows blocks its input to elevated windows.
- **A single-copy program you have open yourself** can take over an agent's start of the same program and show the window on your screen. Most programs only look for a copy within the same Windows session, so this is uncommon; the browsers and Electron apps above are handled.
- **Apps that restore their last state** share it with you, so an agent working in one can touch what you left unsaved there. The Windows 11 Notepad, for example, reopens your tabs.
- **Accessibility helpers are not started.** Windows starts the Java Access Bridge helper (`jabswitch.exe`) at the beginning of a session; it is ended in the first three minutes (see below), so Java applications do not get the bridge to assistive technology in the background session.
- **Scheduled tasks with a logon trigger** also fire for the background session's logon. Console windows they open there in the first three minutes are closed, which ends those second copies.
- Skipping your startup apps relies on undocumented Explorer behaviour (see below) and may stop working after a Windows update. If it does, the desktop still works; your startup apps just open there again.

## What it changes on your system

- Enables child sessions (admin, once). The Remote Desktop service starts on demand; inbound Remote Desktop from the network stays as you had it.
- Stores your account password in Windows Credential Manager under `BgDesk/child-session`. Delete it there at any time. A stored password that stops working is removed automatically.
- While a desktop is starting, writes two per-user autostart entries that launch the agent: `HKCU\Environment\UserInitMprLogonScript` and `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\BgDeskAgent`. Both are removed as soon as the agent answers, and an existing logon script value is restored. Security software may flag the logon script entry.
- Writes volatile keys under `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\<child session id>` so Explorer in the background session skips your startup apps.
- `bgdesk install` edits the chosen agent's MCP configuration and keeps the original as `<name>.bak-bgdesk` beside it.
- In the first three minutes of the background session, ends `jabswitch.exe` (the Java Access Bridge helper that Windows starts at the beginning of a session, and which fails there and puts an error box over an agent's area) and closes "Application Error" boxes that no agent's program put up.
- Keeps state in `%LOCALAPPDATA%\BgDesk`.

The background session does not share your clipboard, drives or printers, and its sound is not played.

## Security

Anything running as your user can connect to the agent's pipe and drive the background session, which runs as you with your files. Treat an agent with this tool as you would treat someone sitting at your account.

## License

MIT
