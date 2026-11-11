# Logging

Lunalyr logs to a rolling file and, at the same time, to an ETW/EventPipe provider named **`Lunalyr`**.

## Writing logs

`Lunalyr.Core.Logging.Logger` is the API (in Core, so ported Mac logic can log too). Categories match the Mac app's
`queue` and `sync`, plus `lyrics`, `player`, `render`, `app` and `settings`. Levels are `debug`, `info`, `warn`
and `error`.

```csharp
await using var logging = LoggingHost.Create(AppPaths.LogsFolder(AppPaths.DefaultDataRoot()), LogLevel.Info);
var log = logging.Logger;

log.Sync.Info("Matched lyrics, offset 120 ms");
log.Queue.Warn("Skipped a track with no lyrics");
log.Player.Error("Session lost", exception);
// Dispose (or FlushAsync) at shutdown so the last entries reach the file.
```

Logging never throws into the caller and never blocks it: entries go onto a bounded queue (8192 entries) and one
background task writes them. If the queue fills up, new entries are dropped and the file gets a
`N log entries were dropped` warning. `Logger.MinimumLevel` can be changed at run time.

## Files

- Folder: `%LOCALAPPDATA%\Lunalyr\Logs\` (the app passes it in; tests use temp folders).
- One UTF-8 file per local day: `lunalyr-2026-11-12.log`. When a file reaches 10 MB the next one is
  `lunalyr-2026-11-12.1.log`, then `.2.log` and so on.
- The last 7 days are kept; older `lunalyr-*.log` files are deleted at start-up and when the day changes. Other files
  in the folder are left alone.
- The files are opened with read/write/delete sharing, so you can read them while the app runs.

Each entry is one line, tab-separated, with the local time and UTC offset:

```
2026-11-12T10:15:30.123+01:00	INFO	sync	Matched lyrics, offset 120 ms
2026-11-12T10:15:31.004+01:00	ERROR	player	Session lost
	System.InvalidOperationException: ...
	   at ...
```

Extra message lines and exception text follow on **continuation lines that start with a tab**, so every entry
starts with a digit and filtering never separates a stack trace from its entry.

## Reading logs: `tools/lunalyr-log.ps1`

Works in Windows PowerShell 5.1 and PowerShell 7. It reads the files as UTF-8, so lyrics and titles in any script
show correctly.

```powershell
tools\lunalyr-log.ps1                              # everything in the last 7 days
tools\lunalyr-log.ps1 -Category sync -Since 10m    # sync entries from the last 10 minutes
tools\lunalyr-log.ps1 -Level warn -Follow          # warnings and errors, live (Ctrl+C to stop)
tools\lunalyr-log.ps1 -Category queue,sync -Tail 50
tools\lunalyr-log.ps1 -Since "2026-11-12 09:30" -Level info
tools\lunalyr-log.ps1 -Path D:\copy-of-someones-logs   # a log folder somebody sent you
```

| Parameter | Meaning |
|---|---|
| `-Category` | One or more of `app`, `queue`, `sync`, `lyrics`, `player`, `render`, `settings` |
| `-Level` | Minimum level: `debug` (default, everything), `info`, `warn`, `error` |
| `-Since` | A duration (`30s`, `10m`, `2h`, `1d`) or a date/time |
| `-Follow` | Keep printing new entries as they're written, across day and size rollovers |
| `-Tail N` | Only the last N matching entries (before following) |
| `-Path` | Log folder; defaults to `%LOCALAPPDATA%\Lunalyr\Logs` |

Output is the original lines, so it can be piped to `Select-String`, `Out-File` and so on.

## ETW / EventPipe: provider `Lunalyr`

Every entry the logger accepts (at or above `MinimumLevel`) is also written to the `Lunalyr` EventSource, whatever
the file does. When no trace session is listening this costs one check per entry.

| | |
|---|---|
| Provider name | `Lunalyr` |
| Provider GUID | `aabe7a04-cf7b-5b6d-7a70-4b7caec9dc02` (derived from the name; a unit test keeps this line correct) |
| Events | `Debug` (1, Verbose), `Info` (2, Informational), `Warn` (3, Warning), `Error` (4, Error) |
| Payload | `category`, `message`, `exception` (empty when there is none) |

Collecting:

```powershell
# EventPipe, no admin needed
dotnet-trace collect -p <pid> --providers Lunalyr            # everything
dotnet-trace collect -p <pid> --providers Lunalyr:0:3        # warnings and errors only (level 3)

# ETW with PerfView (admin)
PerfView collect /OnlyProviders=*Lunalyr

# ETW with WPR: add the provider to a custom .wprp profile by name or GUID
#   <EventProvider Id="Lunalyr" Name="aabe7a04-cf7b-5b6d-7a70-4b7caec9dc02" />
```

Filter by category in the viewer using the `category` payload field.

### Native AOT

Native AOT turns EventSource off unless the app opts in. The app project must set:

```xml
<EventSourceSupport>true</EventSourceSupport>
```

Without it the file log still works and the `Lunalyr` provider is silent. The logging and settings code was checked
with a throwaway `PublishAot` console app (with `EventSourceSupport` on): no trim/AOT warnings, the native binary
wrote the log file, and an in-process `EventListener` received the `Lunalyr` events. Collecting from outside the
process with `dotnet-trace`, PerfView or WPR against the AOT app has not been tried yet.
