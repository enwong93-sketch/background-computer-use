---
name: bgdesk
description: Operate desktop GUI apps in the background with the bgdesk MCP server so the user's screen, pointer and keyboard stay free. Use whenever a task needs clicking, typing or dragging in a Windows app that has no CLI, API or dedicated MCP server, and before reaching for foreground Computer Use.
---

# bgdesk: background GUI automation

`bgdesk` is an MCP server that gives you a private area of a background Windows desktop, called a layer: a 1440x900 screen of your own. The user never sees it and no other agent shares it. Use it instead of foreground Computer Use for GUI work.

## When to use which

- **bgdesk**: any desktop app that can only be operated through its GUI.
- **Foreground Computer Use**: only when the task needs a window that is already open on the user's own desktop, an elevated (administrator) window, or when the user asks for it.
- **Neither**: when a CLI, API or dedicated MCP server can do the job.

## How to work

1. Start the app with `launch` (a program, a document, a folder or a web address). Your layer only shows what you start on it. The user's open windows are not there.
2. Take a `screenshot`. Coordinates for every other tool are pixels of that image.
3. Act with `click`, `drag`, `scroll`, `key`, `type`. Take a new screenshot after an action that changes the screen before choosing the next coordinates.
4. `list_windows` gives titles and rectangles when you need to find or confirm a window.
5. Call `desktop_stop` when the GUI work is finished. It closes your layer and everything on it.

## Things to know

- Browsers and Electron apps (Chrome, Edge, VS Code, Cursor) start with a fresh profile on your layer: no logins, no saved settings. If the task needs the user's logged-in session, say so instead of trying to log in.
- If `launch` reports that the program opened no window, it passed the work to a copy that is already running (for example in another agent's area). Do not retry in a loop; tell the user.
- Shortcuts that act on the whole screen (Win key combinations other than Win+R and Win+E, Alt+Tab, Alt+Esc, Ctrl+Esc) are refused, because other agents share the screen. Use the window's own controls. There is no taskbar: a minimized window is restored at once, and a maximized one fills your area.
- Actions are routed for you. If a result says `ok, but nothing visibly changed` and you expected a change, take a screenshot; if the app ignored the action, repeat the same call with `input=real`.
- Elevated windows cannot be driven.
- Files are the user's real files. Saving, deleting or overwriting there is real.
- The first call takes several seconds while the background session starts.