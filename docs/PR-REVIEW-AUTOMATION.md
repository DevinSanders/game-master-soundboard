# Antigravity PR Review & Auto-Merge Workflow

This repository uses **Google Antigravity** via GitHub Actions to provide automated AI code reviews on all pull requests and safe auto-merging for dependency updates (Dependabot).

---

## 🌟 How It Works

1. **Triggering**:
   - Runs automatically on any pull request opened, updated, or marked ready for review (`pull_request_target`).
   - Runs on completion of the `.NET 10 Build & Test` matrix (`workflow_run`) to complete auto-merges as soon as CI passes.
   - Can be manually triggered on any PR via GitHub Actions (`workflow_dispatch`).

2. **Architectural Review**:
   - Evaluates the pull request diff against the repository's architecture standards in `.github/copilot-instructions.md`:
     - Clean separation: `Desktop -> UI -> Core -> PluginApi`.
     - Zero UI framework references in `Core`.
     - Cross-platform audio rules (reflection for Windows `NAudio.Wasapi` / `NAudio.WinMM`, OpenAL for Linux/macOS).
     - `WaveBuffer` aliasing safety in the audio pipeline.
     - SQLite migrations appended to `SchemaMigrations.cs`.
     - MVVM conventions (`[ObservableProperty]`, `[RelayCommand]`, compiled bindings).
   - Generates a structured review comment on the PR with verdict (`APPROVE`, `COMMENT`, or `REQUEST_CHANGES`).

3. **Auto-Merge Policy**:
   - **Dependabot / Dependency Updates**:
     - Automatically approved and merged (`squash` + branch deletion) **ONLY IF**:
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
- **Use Case**: Ideal for CI turnarounds, quick diff analysis, and dependency evaluations.
- **Fallback**: Automatically falls back to `gemini-1.5-flash` if rate limits occur.

### Advanced Model: `gemini-2.5-pro` (Recommended for Complex PRs)
If you want deep reasoning, nuanced architectural analysis, and complex code reviews:
- Set repository variable `GEMINI_MODEL`:
  ```bash
  gh variable set GEMINI_MODEL --body "gemini-2.5-pro"
  ```
  *(Or in GitHub: **Settings** > **Secrets and variables** > **Actions** > **Variables** tab > `GEMINI_MODEL = gemini-2.5-pro`)*.

---

## 🔑 Setup: Adding `GEMINI_API_KEY`

To enable the AI review engine, add a Gemini API key to your GitHub repository secrets:

1. **Get an API Key**:
   - Visit [Google AI Studio](https://aistudio.google.com/app/api-keys) and create a free Gemini API key.

2. **Add to Repository Secrets**:
   - Using the GitHub CLI:
     ```bash
     gh secret set GEMINI_API_KEY
     ```
     *(Paste your API key when prompted)*
   - Or via GitHub Web UI:
     - Navigate to **Settings** > **Secrets and variables** > **Actions**.
     - Click **New repository secret**.
     - Name: `GEMINI_API_KEY`
     - Value: *(Your Gemini API key)*
     - Click **Add secret**.

---

## 🧪 Testing the Workflow

You can manually trigger the review on any open PR using the GitHub CLI:
```bash
gh workflow run "Antigravity PR Review & Auto-Merge" -f pr_number=56
```
Or navigate to the **Actions** tab in GitHub, select **Antigravity PR Review & Auto-Merge**, click **Run workflow**, and input the PR number.
