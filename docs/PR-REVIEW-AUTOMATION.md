# Antigravity PR Review, Conflict Resolver & Auto-Merge Engine

This repository uses **Google Antigravity** via GitHub Actions to provide automated merge conflict resolution, AI code reviews, and serialized queue auto-merging for pull requests.

---

## 🌟 How It Works

### 1. Serialized Merge Queue
When multiple PRs are open at once, GitHub Actions uses a strict single-runner queue (`concurrency: group: antigravity-pr-queue, cancel-in-progress: false`).
- **One PR at a Time**: Each PR waits for any preceding PRs to finish resolving, reviewing, building, and merging into `main`.
- **Automatic Succession**: Once a PR merges into `main`, Antigravity automatically dispatches the next queued PR to synchronize with the new `main`.

### 2. Automated Conflict Resolution
When a PR is behind `main` or conflicts with changes merged by prior PRs:
- Antigravity checks out the PR branch and attempts a 3-way merge from `origin/main`.
- If Git detects merge conflicts, Antigravity identifies the conflicted files and submits each conflict to Gemini.
- Gemini reconciles the differences according to repository rules (e.g., maintaining dependency alignment across projects, preserving code logic, removing conflict markers cleanly).
- Antigravity commits the resolved files and pushes them to the PR branch (`github-actions[bot]`).
- Antigravity comments on the PR summarizing the resolved files and triggers a fresh CI run.

### 3. Architectural Code Review
- Evaluates the pull request diff against the repository's architecture standards in `.github/copilot-instructions.md`:
  - Layered dependency flow: `Desktop -> UI -> Core -> PluginApi`.
  - Zero UI framework references in `Core`.
  - Cross-platform audio rules (reflection for Windows `NAudio.Wasapi` / `NAudio.WinMM`, OpenAL for Linux/macOS).
  - `WaveBuffer` aliasing safety in the audio pipeline.
  - SQLite migrations appended to `SchemaMigrations.cs`.
  - MVVM conventions (`[ObservableProperty]`, `[RelayCommand]`, compiled bindings).
- Generates a structured review comment on the PR with verdict (`APPROVE`, `COMMENT`, or `REQUEST_CHANGES`).

### 4. Auto-Merge Policy
- **Dependabot / Dependency Updates**:
  - Automatically squashed, merged, and deleted **ONLY IF**:
    1. Antigravity AI review passes (`APPROVE`) with no breaking API changes or package downgrade warnings (`NU1605`).
    2. All CI matrix checks (`Build (windows-latest)`, `Build (ubuntu-latest)`, `Build (macos-latest)`) pass.
- **Feature / Code PRs**:
  - Antigravity posts the detailed review and recommendations, but retains merge authority for human maintainers.

---

## 🧠 Gemini Models & Google AI Tiers

The action interfaces with the Gemini Developer API (via your `GEMINI_API_KEY` from Google AI Studio).

### Default Model: `gemini-2.5-flash`
- **Speed**: Ultra-fast (~2–4 seconds).
- **Context Window**: 1M tokens.
- **Use Case**: Routine CI turnarounds, fast conflict resolution, and dependency evaluations.

### Advanced Model: `gemini-2.5-pro` (Recommended for Complex PRs)
For deep architectural analysis:
- Set repository variable `GEMINI_MODEL`:
  ```bash
  gh variable set GEMINI_MODEL --body "gemini-2.5-pro"
  ```

---

## 🔑 Setup: Adding `GEMINI_API_KEY`

To enable the AI engine:
1. Visit [Google AI Studio](https://aistudio.google.com/app/api-keys) and obtain a Gemini API key.
2. In your terminal, run:
   ```bash
   gh secret set GEMINI_API_KEY
   ```
   *(Paste your API key when prompted)*.

---

## 🧪 Manual Queue Trigger

To run the queue engine on any open PR:
```bash
gh workflow run "Antigravity PR Review & Auto-Merge" -f pr_number=58
```
