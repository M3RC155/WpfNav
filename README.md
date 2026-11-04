# WpfNav

WpfNav is a local MCP server that helps Claude Code and other MCP clients navigate large WPF codebases (C# and XAML) with fewer tokens. It parses `.cs` files with Roslyn's syntax API and reads `.xaml` and project files as XML, so it never builds or restores your solution. It exposes five read-only tools. Every result stays within an estimated-token budget, which each call can lower, and long outlines and source continue page by page. An in-memory index, kept current by a file watcher, backs the repo-wide lookups.

## Build and test

```powershell
dotnet test
```

The corpus smoke test generates a WPF codebase of about 2,650 files in a temp folder and runs every tool against it, so the suite needs nothing outside this repo.

## Publish

Close every client session that uses wpfnav first (the running exe is locked), then run:

```powershell
dotnet publish src/WpfNav.Server -c Release -o "$env:LOCALAPPDATA\WpfNav"
```

## Register with Claude Code

The server serves one repository root. Name it explicitly with `--root <repository>`:

- **One repo, just for you (local scope), with an explicit root (recommended):**

  ```powershell
  claude mcp add --scope local wpfnav -- "$env:LOCALAPPDATA\WpfNav\WpfNav.Server.exe" --root "D:\work\YourApp"
  ```

- **Every repo you open (user scope).** With no `--root`, the root is the server's working directory. Claude Code usually starts the server in the folder you opened, but that is the launcher's choice, not WpfNav's:

  ```powershell
  claude mcp add --scope user wpfnav -- "$env:LOCALAPPDATA\WpfNav\WpfNav.Server.exe"
  ```

- **Shared through the repo (project scope).** Add a `.mcp.json` file; each developer needs the exe at the same path:

  ```json
  {
    "mcpServers": {
      "wpfnav": {
        "command": "C:\\Users\\<you>\\AppData\\Local\\WpfNav\\WpfNav.Server.exe",
        "args": ["--root", "."]
      }
    }
  }
  ```

Server options:

| Option | Meaning |
|---|---|
| `--root <repo>` | Repository root. Omitted, or `--root .`, resolves from the server's working directory. A relative path resolves from there too. |
| `--exclude <glob>` | Leave matching files out. Repeatable, for example `--exclude "legacy/**"`. |
| `--log-level <level>` | `Trace`, `Debug`, `Information` (default), `Warning` or `Error`. |

WpfNav doesn't search upward for a Git root. If you deliberately serve a broad root, such as a folder holding several checkouts, leave out what you don't need:

```powershell
claude mcp add --scope local wpfnav -- "$env:LOCALAPPDATA\WpfNav\WpfNav.Server.exe" --root "D:\work" --exclude "archive/**" --exclude "**/generated/**"
```

Logs go to stderr; stdout carries only MCP messages. If `claude` isn't on your PATH, it is usually at `%USERPROFILE%\.local\bin\claude.exe`.

## Register with other clients

WpfNav is a plain stdio MCP server, so any MCP client can run it: give the client the exe as the command and `--root <repository>` plus any of the server options above as arguments. In each example, replace `D:\work\YourApp` with your repository and `<you>` with your Windows user name.

In Windows PowerShell, keep the `--` in quotes (`'--'`) in the CLI commands below. npm installs these CLIs as `.ps1` scripts, and PowerShell drops a bare `--` before it reaches a script, so the server arguments would be read as the CLI's own options.

### OpenAI Codex CLI

```powershell
codex mcp add wpfnav '--' "$env:LOCALAPPDATA\WpfNav\WpfNav.Server.exe" --root "D:\work\YourApp"
codex mcp list
```

This writes to `%USERPROFILE%\.codex\config.toml`, which you can also edit by hand:

```toml
[mcp_servers.wpfnav]
command = 'C:\Users\<you>\AppData\Local\WpfNav\WpfNav.Server.exe'
args = ['--root', 'D:\work\YourApp']
```

### Gemini CLI

```powershell
gemini mcp add --scope user wpfnav "$env:LOCALAPPDATA\WpfNav\WpfNav.Server.exe" '--' --root "D:\work\YourApp"
gemini mcp list
```

`--scope user` writes to `%USERPROFILE%\.gemini\settings.json`. Leave it out to write to the current folder's `.gemini\settings.json` instead.

### GitHub Copilot CLI

```powershell
copilot mcp add wpfnav '--' "$env:LOCALAPPDATA\WpfNav\WpfNav.Server.exe" --root "D:\work\YourApp"
```

This writes to `%USERPROFILE%\.copilot\mcp-config.json`. Check it with `/mcp` inside a Copilot CLI session.

### VS Code (GitHub Copilot agent mode)

Add `.vscode/mcp.json` to the repository. VS Code fills in both variables, so the file works for anyone who published the exe to the default path:

```json
{
  "servers": {
    "wpfnav": {
      "type": "stdio",
      "command": "${env:LOCALAPPDATA}\\WpfNav\\WpfNav.Server.exe",
      "args": ["--root", "${workspaceFolder}"]
    }
  }
}
```

For every workspace, run **MCP: Open User Configuration** and add the same entry without `args`: VS Code starts the server in the open workspace folder, which then becomes the root. Start the server from the CodeLens in `mcp.json` or with **MCP: List Servers**.

### Visual Studio 2022 (17.14+) and 2026

Put this in `.vs\mcp.json` in the solution folder (just you, this solution). `%USERPROFILE%\.mcp.json` loads it for every solution, but `--root` still names one repository:

```json
{
  "servers": {
    "wpfnav": {
      "type": "stdio",
      "command": "C:\\Users\\<you>\\AppData\\Local\\WpfNav\\WpfNav.Server.exe",
      "args": ["--root", "D:\\work\\YourApp"]
    }
  }
}
```

Use absolute paths here: Visual Studio's docs don't describe variables or which folder it starts servers in. Its tools start disabled, so switch Copilot Chat to **Agent** mode and turn on the wpfnav tools in the **Tools** picker. Visual Studio also loads `.vscode\mcp.json` and `.cursor\mcp.json` from the solution folder. If one of those makes wpfnav fail to start in Visual Studio, give that entry an absolute `--root`.

### Cursor

Add `.cursor/mcp.json` to the repository. Cursor sets `${workspaceFolder}` to the folder that holds `.cursor`:

```json
{
  "mcpServers": {
    "wpfnav": {
      "type": "stdio",
      "command": "${env:LOCALAPPDATA}\\WpfNav\\WpfNav.Server.exe",
      "args": ["--root", "${workspaceFolder}"]
    }
  }
}
```

## Check it

```powershell
claude mcp list
```

The `wpfnav` line should say `✓ Connected`. Then check which repository it serves: every result starts with a `root:` line, for example

```text
root: D:\work\YourApp
```

If that isn't the repository you meant, fix `--root` in the registration. The tools' `scope` and `path` filters only narrow results within the configured root; they never move it. Every path in a result is relative to that root.

To call the tools by hand, use MCP Inspector:

```powershell
npx @modelcontextprotocol/inspector "$env:LOCALAPPDATA\WpfNav\WpfNav.Server.exe" --root <repo>
```

## Tools

| Tool | Use it for |
|---|---|
| `overview(scope?, max_tokens?)` | A map of the repo, a project or a folder: projects in dependency order, packages, entry points, folders, type roles. Use it when the structure is unfamiliar. |
| `outline(paths[], level?, offset?, entry_offset?, max_tokens?)` | The structure of .cs/.xaml files without reading them: members with signatures and line ranges, XAML resources, bindings and named elements. Use it to inspect structure or pick members. |
| `find_symbol(query, kind?, project?, path?, derives_from?, limit?, offset?, max_tokens?)` | Declarations by name, camelCase initials (`OEVM`), `Type.Member` or wildcard, including XAML `x:Key` and `x:Name`. Use it to discover or disambiguate. |
| `read_symbol(symbol, path?, line_offset?, line_limit?, column_offset?, max_tokens?)` | The source of one member or type, with doc comments and line numbers. Call it directly when you know the target. |
| `search_code(pattern, literal?, whole_word?, case_sensitive?, file_kind?, project?, path?, limit?, offset?, max_tokens?)` | A grep whose hits are grouped by file and containing member, with one sample line per group. |

No tool has to come first: a known symbol can be read straight away, and `outline` isn't required before `read_symbol`.

Exact find-references and rename aren't included. Claude Code's LSP tool with the `csharp-lsp` plugin covers those.

## Budgets

Each tool has a default budget, which is also its maximum, in estimated tokens:

| Tool | Default and maximum |
|---|---:|
| `overview` | 2,500 |
| `outline` | 3,000 |
| `find_symbol` | 2,000 |
| `read_symbol` | 4,000 |
| `search_code` | 2,500 |

Pass `max_tokens` from 256 up to that maximum for a smaller result. Values outside the range return an error result that names the range; they aren't clamped. A small-budget direct read:

```text
read_symbol(symbol="OrderEditViewModel.SaveAsync", max_tokens=400)
```

What the budget covers: all the text WpfNav writes, including the `root:` line, status and index warnings, headers, repeated context, continuation notes and WpfNav's own error messages, measured together. It doesn't cover the MCP JSON envelope, the tool schemas, or argument-binding and protocol errors produced by the MCP SDK.

The estimate is `ceil(characters / 3.5)`, counting UTF-16 characters. It is not an exact Claude or OpenAI token count, and real token counts can be higher, especially for dense code or non-Latin text. Changing the estimator needs measurements against a real tokenizer first. If the root line, a declaration header or one entry can't fit the chosen budget, the call fails with an error asking for a larger `max_tokens`, rather than returning partial metadata.

## Continuations

Long results end with a note that names the next position. Repeat the same call with the numbers from that note.

**Outline entries.** `offset` counts resolved files to skip. `entry_offset` counts outline rows (types, members, `fields:` rows, resources, `merged:` rows, XAML tree rows) to skip in the first remaining file. Each page repeats the file header and the enclosing namespace and types, marked `(continued)`, which don't count as entries.

```text
outline(paths=["src/Orders/OrderGrid.cs"], max_tokens=256)

root: D:\work\YourApp
src/Orders/OrderGrid.cs (204 lines)
namespace Orders
  public class OrderGrid [2-204]
    public void Method000(int a, string b) [4]
    …
    public void Method012(int a, string b) [16]
… 187 more entries in this file; repeat the same paths/level with offset=0, entry_offset=14

outline(paths=["src/Orders/OrderGrid.cs"], offset=0, entry_offset=14, max_tokens=256)

root: D:\work\YourApp
src/Orders/OrderGrid.cs (204 lines)
namespace Orders (continued)
  public class OrderGrid [2-204] (continued)
    public void Method013(int a, string b) [17]
    …
```

Once a file is finished and more remain, the note says `offset=<next file>, entry_offset=0`. Files come in the order you requested them. A folder expands in case-insensitive path order, and a file named twice appears once.

**Source lines.** A member too large for the budget returns its first page. A type that's too large returns its outline, with hints to read one member or page the whole type with `line_offset=0`. `line_offset` counts from the declaration's first line, including doc comments and attributes. `line_limit` caps the lines on a page.

```text
read_symbol(symbol="Importer.Run", max_tokens=256)

root: D:\work\YourApp
src/Orders/Importer.cs:4-307  Orders.Importer.Run
4	    /// <summary>Imports.</summary>
5	    public void Run()
…
24	        Console.WriteLine(17);
… lines 4-24 of 4-307 shown; repeat the same target with line_offset=21, column_offset=0
```

A single line too long for any page is split into fragments labelled with their columns, such as `123 [cols 0..2400)`, and the note then gives a nonzero `column_offset` for the same line. Fragments never split a surrogate pair. Line numbers are absolute and text is shown with LF line endings. The last page ends with `(… shown; end of declaration)`.

Positions refer to the files as they are on disk now: every page re-reads the file. If you edit a file while paging through it, start again from offset zero.

## Eval

`eval/run.ps1` runs the same tasks through `claude -p` with and without wpfnav, then compares tokens, turns and answers. The tasks in `eval/tasks.json` ask about [microsoft/WPF-Samples](https://github.com/microsoft/WPF-Samples), so pass `-Repo` the folder you cloned it to:

```powershell
powershell -ExecutionPolicy Bypass -File eval\run.ps1 -Repo D:\work\WPF-Samples -Runs 3
```

Pass `-Only <task id>` to run a single task. Each task costs two Claude Code sessions per run. Results go to `eval/results/<timestamp>/`, which git ignores: `runs.csv`, one answer file per run, and `summary.md`.
