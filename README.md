# UnsubAutoATC

A [vPilot](https://vpilot.rosscarlson.dev/) plugin that stops VATSIM's
**AUTO_ATC** private messages from jumpscaring you.

By default you still get the AUTO_ATC message with your squawk code when you
connect, and everything else AUTO_ATC sends is quietly dropped. You can also
keep every message but turn off the sound, or apply the same rules to any
other callsign.

## Install

1. Download `UnsubAutoATC-x.y.z.zip` from the
   [latest release](https://github.com/yanjz124/UnsubAutoATC/releases/latest).
2. **Unblock the zip before extracting it.** Right-click the zip, choose
   **Properties**, tick **Unblock** and click **OK**. If you skip this,
   Windows marks the DLL as downloaded from the internet and vPilot silently
   refuses to load it.
3. Extract `UnsubAutoATC.dll` and `UnsubAutoATC.ini` into your vPilot
   Plugins folder. To open it, press <kbd>Win</kbd>+<kbd>R</kbd> and paste:
   ```
   %LOCALAPPDATA%\vPilot\Plugins
   ```
4. Restart vPilot.

To check it's working, open `UnsubAutoATC.log` in the same folder after
vPilot starts. You should see a line like
`UnsubAutoATC: hooked 1 message publisher(s), 2 sound publisher(s).` If the
log file doesn't appear, see [Troubleshooting](#troubleshooting).

To update, close vPilot and replace `UnsubAutoATC.dll`. Keep your
`UnsubAutoATC.ini`.

To uninstall, delete `UnsubAutoATC.dll`, `UnsubAutoATC.ini` and
`UnsubAutoATC.log` from the Plugins folder.

## Configure

Open `UnsubAutoATC.ini` in Notepad. Changes apply to the next message from a
listed sender, so you don't need to restart vPilot.

Each message from a listed sender gets one of three actions:

| Action | What happens |
|---|---|
| `show` | Normal message with sound |
| `mute` | The message appears, but with no sound |
| `hide` | The message never appears (it's still written to the log) |

The action is picked like this:

1. If `MuteAll=true`, the message is muted.
2. Otherwise, if it contains a `Whitelist` keyword, it gets `WhitelistMode`.
3. Otherwise, it gets `Mode`.

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | `false` turns the plugin off |
| `Senders` | `AUTO_ATC` | Callsigns to filter, comma-separated. Any callsign works |
| `MuteAll` | `false` | Mute every message from those senders and ignore the settings below |
| `Whitelist` | `squawk` | Keywords, comma-separated. Whole words, not case-sensitive |
| `WhitelistMode` | `show` | Action for messages with a whitelist keyword |
| `Mode` | `hide` | Action for all other messages |
| `Log` | `true` | Write each filtered message and its action to `UnsubAutoATC.log` |

Messages from callsigns that aren't in `Senders` are never filtered.

### Examples

| I want... | Settings |
|---|---|
| The squawk message only (default) | `Mode=hide`, `WhitelistMode=show` |
| The squawk message only, silently | `Mode=hide`, `WhitelistMode=mute` |
| Every message, silently | `MuteAll=true` |
| Every message, with sound only for the squawk | `Mode=mute`, `WhitelistMode=show` |
| To silence another callsign as well | `Senders=AUTO_ATC, N123AB` |

### Trying it out with a friend

Add a friend's callsign to `Senders` (for example `Senders=AUTO_ATC, N123AB`),
save, and have them send you private messages:

- `hello` should be hidden (or muted, depending on `Mode`).
- `squawk 1234` should show.

Each message and what the plugin did with it appears in `UnsubAutoATC.log`.
Remove their callsign when you're done.

## Troubleshooting

**No `UnsubAutoATC.log` after starting vPilot.** vPilot didn't load the
plugin. Usually the DLL is still blocked: close vPilot, right-click
`UnsubAutoATC.dll`, choose **Properties**, tick **Unblock**, and start vPilot
again. Also check that the DLL sits directly in the `Plugins` folder, not in
a subfolder.

**The log says `hooked 0 message publisher(s)`.** The plugin loaded but
couldn't hook into your vPilot version. Please
[open an issue](https://github.com/yanjz124/UnsubAutoATC/issues) with your
vPilot version.

**I hid something I needed.** Hidden messages are still in `UnsubAutoATC.log`.
Set `Enabled=false` to switch the plugin off immediately.

## How it works

vPilot's plugin API lets plugins see events but not cancel them. Plugins run
inside vPilot, though, and vPilot passes events through an internal event bus.
UnsubAutoATC filters two of them:

- **Incoming private messages.** A hidden message is stopped here, so vPilot
  never shows it, plays a sound or flashes the taskbar. Other plugins (such as
  notification relays) don't receive it either.
- **Sound requests.** After a muted message, vPilot's message sounds are
  skipped for 3 seconds. Radio, SELCAL and other sounds still play.

vPilot's code is obfuscated, so the plugin doesn't rely on its internal names.
It finds what it needs through the event bus itself, so it should keep working
across vPilot updates. It was developed and tested on vPilot 3.12.1.

## Building from source

You need the .NET SDK (8 or later) on Windows.

```
dotnet build src/UnsubAutoATC -c Release
```

This builds against `RossCarlson.Vatsim.Vpilot.Plugins.dll` from your local
vPilot install (`%LOCALAPPDATA%\vPilot`). To use another location, add
`-p:VpilotDir=<folder>`.

That DLL is part of vPilot and isn't included in this repository. CI builds
against a stub with the same API instead (`ci/PluginsStub`). Pushing a `v*`
tag builds the plugin and publishes a GitHub release with the zip.

## Disclaimer

UnsubAutoATC isn't affiliated with VATSIM or vPilot. AUTO_ATC messages exist
to help you; if you hide them, you're still responsible for flying by the
network's rules.

## License

[MIT](LICENSE)
