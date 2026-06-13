# WinNotifyApi

Local Windows notification API built with C# and ASP.NET Core.

## Run

```powershell
dotnet run
```

The service listens on:

```text
http://127.0.0.1:8787
```

## Notify

```powershell
Invoke-RestMethod `
  -Method Post `
  -Uri http://127.0.0.1:8787/notify `
  -ContentType 'application/json' `
  -Body '{"title":"提醒","message":"这是一条来自 API 的提示","durationMs":5000}'
```

Request fields:

- `title`: optional window title.
- `message`: notification body.
- `text`: alias for `message`.
- `durationMs`: optional display duration, clamped between 1000 and 60000.

## Notes

- This is intended to run as a normal desktop user process.
- If it runs as a Windows Service in Session 0, the UI window may not appear in the logged-in desktop session.
- The service binds only to `127.0.0.1` by default.
