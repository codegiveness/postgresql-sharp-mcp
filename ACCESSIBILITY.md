# Accessibility

postgresql-sharp-mcp has no graphical interface of its own. People reach it through three surfaces:

- **Your MCP client.** The server exchanges JSON-RPC messages with the client over stdio. How tool results are shown, read aloud or navigated is decided by the client, not by this project.
- **The command line.** `--help`, `--version` and `--validate` print plain text without colors or terminal control codes, so screen readers and braille displays receive the text as written.
- **Documentation.** [README.md](README.md), [CONTRIBUTING.md](CONTRIBUTING.md), [SECURITY.md](SECURITY.md) and [docs/](docs/) are Markdown rendered by GitHub. Badges carry text alternatives; commands and configuration are text code blocks rather than screenshots.

## Known limitations

- The quick start reads the connection string at a hidden prompt (`read -s` in Bash/Zsh, `Read-Host -AsSecureString` in PowerShell). Hidden input is not echoed, so neither the screen nor a screen reader can confirm what was typed before Enter. Run `--validate` afterwards: its exit status and text output tell you whether the connection works.
- The README's [Project badges](README.md#project-badges) table holds about seventy badges. Each has alternative text, but reading the whole table with a screen reader is slow; the five badges at the top of the README cover release, registry, CI and license status.
- Several README reference sections are collapsed `<details>` blocks and must be expanded to be read.

## Reporting a barrier

If something in the documentation, command-line output or setup steps stops you from using the project with your assistive technology, open a [bug report](https://github.com/codegiveness/postgresql-sharp-mcp/issues/new/choose) or ask in [Discussions Q&A](https://github.com/codegiveness/postgresql-sharp-mcp/discussions/categories/q-a). Describe the step, what you expected and the assistive technology you use. Never include credentials, connection strings or database results.
