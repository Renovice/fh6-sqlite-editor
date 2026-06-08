# FH6 SQLite Editor Native

Native Windows/WPF SQLite editor for FH6 database dumps. It replaces the old Python/web wrapper with a normal desktop app, integrated DB open/save dialogs, dump/import buttons, dark mode memory, and focused editors for cars, engines, engine parts, aero, tuning limits, validation, and raw tables.

## What is in this repo

- `New Native WPF Editor/fh6_sqlite_native`: WPF source code.
- `New Native WPF Editor/DB_VALUE_MAP.md`: notes on values that appear to be effect/base/reference/display/menu/visual data.


Put your clean untouched DB locally at:

```text
BASE DB/fh6_db.sqlite
```

That file is used by the editor for compare/reset/import behavior, but it should not be committed.

## Build

Requirements:

- Windows
- .NET SDK 9.0 or compatible

Build the app:

```powershell
dotnet build "New Native WPF Editor/fh6_sqlite_native/FH6SQLiteEditorNative.csproj" -c Release
```

Publish a single-file Windows build:

```powershell
dotnet publish "New Native WPF Editor/fh6_sqlite_native/FH6SQLiteEditorNative.csproj" -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o "New Native WPF Editor/singlepublish"
```

The publish output is local-only and ignored by git.


if you want to Support me buy me a coffee  https://ko-fi.com/grayenjoyer50

## Notes

This is a modding/research utility. Keep backups of your DB dumps and treat live game import/reset features as experimental.


IF you want to support me https://ko-fi.com/grayenjoyer50
