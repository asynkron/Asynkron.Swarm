# Asynkron.Swarm

AI Agent Swarm Orchestrator - Run multiple AI coding agents in parallel on git worktrees.

## Overview

Swarm creates multiple git worktrees from your repository and spawns AI agents (Claude or Codex) to work on tasks in parallel. Agents compete to fix issues, and a supervisor agent evaluates and merges the best solution.

## Installation

```bash
dotnet tool install -g Asynkron.Swarm
```

## Usage

```bash
swarm --repo ~/git/my-project --todo todo.md --agents 3 --minutes 5
```

### Options

| Option | Description | Default |
|--------|-------------|---------|
| `-r, --repo` | Path to the git repository | (required) |
| `-t, --todo` | Name of the todo file | `todo.md` |
| `-a, --agents` | Number of worker agents | `3` |
| `-m, --minutes` | Minutes per round | `5` |
| `--agent-type` | Agent CLI: `Claude` or `Codex` | `Claude` |
| `--max-rounds` | Maximum rounds | `10` |

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
- Claude CLI (`claude`) or Codex CLI (`codex`)

## License

MIT
