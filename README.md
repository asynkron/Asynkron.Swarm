# Asynkron.Swarm

AI Agent Swarm Orchestrator - Run multiple AI coding agents in parallel on git worktrees.

## Overview

Swarm creates multiple git worktrees from your repository and spawns AI agents (Claude or Codex) to work on tasks in parallel. Agents compete to fix issues, and a supervisor agent evaluates and merges the best solution.

## Installation

```bash
dotnet tool install -g Asynkron.Swarm
```

## Typical Usage

The most common workflow is to create a markdown file describing your task, then run swarm from within your repository:

```bash
# Navigate to your repository
cd ~/projects/my-app

# Create a task file describing what you want done
echo "- [ ] Add user authentication with JWT tokens" > task.md

# Run swarm with your task file
swarm --todo task.md
```

Swarm auto-detects the git repository from the current directory. By default, it spawns 2 Claude workers that work in parallel on the task.

You can also specify the repository path explicitly:

```bash
swarm --todo task.md --repo ~/projects/my-app
```

## Usage Examples

```bash
# Basic usage (from within a git repo)
swarm --todo task.md

# With explicit repo path
swarm --todo task.md --repo ~/projects/my-app

# With custom worker configuration
swarm --todo task.md --claude 3 --gemini 2 --minutes 10

# Mixed agent team with custom supervisor
swarm --todo task.md --claude 2 --codex 1 --copilot 1 --supervisor Gemini

# Arena mode (timed rounds with competitive evaluation)
swarm --todo task.md --arena --minutes 5 --claude 4

# Resume a previous session
swarm --resume <session-id>

# Detect which AI agents are installed
swarm --detect
```

### Options

| Option | Description | Default |
|--------|-------------|---------|
| `-r, --repo <PATH>` | Path to the git repository | Current directory |
| `-t, --todo <FILE>` | Name of the todo file (relative to repo root) | `todo.md` |
| `--claude <COUNT>` | Number of Claude worker agents | `2` (if no agents specified) |
| `--codex <COUNT>` | Number of Codex worker agents | `0` |
| `--copilot <COUNT>` | Number of Copilot worker agents | `0` |
| `--gemini <COUNT>` | Number of Gemini worker agents | `0` |
| `-m, --minutes <MINUTES>` | Minutes per round (arena mode) | `15` |
| `--supervisor <TYPE>` | Supervisor agent: `Claude`, `Codex`, `Copilot`, or `Gemini` | `Claude` |
| `--max-rounds <COUNT>` | Maximum number of rounds | `10` |
| `--autopilot` | Runs continuously without timed rounds | `true` (default mode) |
| `--arena` | Timed rounds where supervisor evaluates and picks winning changes | `false` |
| `--resume <SESSION_ID>` | Resume a previous session by its ID | - |
| `--detect` | Detect installed CLI agents and exit | - |
| `--skip-detect` | Skip agent detection at startup | `false` |

## How It Works

### Round Lifecycle

1. **Create Worktrees**: Creates N git worktrees (`round1-agent1`, `round1-agent2`, etc.)
2. **Inject Rivals**: Each worktree's todo.md gets a "Rivals" section with paths to other worktrees
3. **Start Workers**: Spawns AI agents, one per worktree
4. **Start Supervisor**: Supervisor agent monitors worker logs
5. **Wait**: Workers compete for the specified time
6. **Kill Workers**: All workers are terminated, logs marked with `<<worker has been stopped>>`
7. **Evaluate**: Supervisor evaluates each worktree (build, tests)
8. **Merge**: Supervisor picks winner and merges to main repo
9. **Cleanup**: Worktrees are deleted
10. **Repeat**: If todo items remain, start next round

### UI

The TUI shows:
- **Left panel**: List of running agents (use ↑/↓ to select)
- **Right panel**: Log output of selected agent

Press `q` to quit.

## Todo File Format

The todo file should contain markdown checkboxes:

```markdown
# Tasks

- [ ] Fix authentication bug in login.ts
- [ ] Add unit tests for UserService
- [ ] Optimize database queries
```

## Requirements

- .NET 9.0+
- Git
- At least one AI coding CLI installed:
  - Claude CLI (`claude`)
  - Codex CLI (`codex`)
  - Copilot CLI (`copilot`)
  - Gemini CLI (`gemini`)

Use `swarm --detect` to check which agents are available on your system.

## License

MIT
