# Lock behavior before upgrading

Capture the current behavior of untested code as runnable tests **before** any upgrade
transformation, then prove after the upgrade that the behavior is unchanged.

## Prerequisite

The `pinion` CLI must be installed:

```pwsh
dotnet tool install -g Pinion
pinion --version
```

If `pinion --version` fails, stop and tell the user to install it. Do not skip this instruction and
proceed with the upgrade — the whole point is that the upgrade is unsafe without it.

## Why this exists

An upgrade is validated by "the build and tests pass". On a codebase with no tests, that check is
vacuous: every test can pass while behavior changes silently. Pinion records what the code *actually
does today* — bugs included — as golden-master tests, so an unintended change becomes a failing test
instead of a production incident.

Pinion's default generator is deterministic and runs entirely on the local machine. It sends nothing
anywhere, so this step adds no data-handling concerns to the upgrade.

## Step logic

### Before transforming any code

1. If the repository has uncommitted changes, stop and ask the user to commit or stash first. Golden
   masters must describe a known-good starting state.

2. Run the readiness audit on the code being upgraded:

   ```pwsh
   pinion analyze <path-to-.csproj-or-.sln>
   ```

   If the report shows **0 high-risk unprotected methods**, record that in the assessment and skip to
   the upgrade — there is nothing worth locking.

3. Otherwise lock the riskiest behaviors. If the project has no characterization test project yet, use
   `quickstart`, which analyzes, scaffolds a test project, and characterizes in one step:

   ```pwsh
   pinion quickstart <path-to-.csproj> --top 10
   ```

   If a characterization test project already exists, use it instead of scaffolding another:

   ```pwsh
   pinion generate <path-to-.csproj> -p <path-to-test.csproj> --top 10
   ```

4. Commit the generated tests and golden masters as their own commit, before any upgrade change:

   ```pwsh
   git add **/PinionCharacterization
   git commit -m "Lock current behavior before upgrade (Pinion golden masters)"
   ```

   Record in `assessment.md` how many methods were locked and which test project holds them.

### After the upgrade completes

5. Re-run the locked behavior against the upgraded code. **`verify` takes the TEST project, not the
   code project** — this is the most common mistake:

   ```pwsh
   pinion verify <path-to-test.csproj>
   ```

6. If `verify` reports **identical**, the upgrade preserved behavior. Note it in `tasks.md` and finish.

7. If `verify` reports **changed behaviors**, treat the upgrade task as FAILED. Do not mark it
   complete. Show the user the diff verbatim: it names each method, the input, the old output and the
   new output. Then, for each changed behavior, either
   - fix the upgraded code so the original behavior is restored, and run `verify` again; or
   - ask the user whether the change is intended.

## Do not do this

**Never run `pinion accept` to make a failing `verify` pass.** `accept` re-baselines the golden master
to the current output, which marks the new behavior as correct. Running it to clear a red check erases
exactly the evidence this instruction exists to produce, and it does so silently.

`accept` is only correct when a human has reviewed the diff and confirmed the change was intended. If
that happens, scope it to the specific behavior rather than accepting everything:

```pwsh
pinion accept <path-to-test.csproj> --name <ClassOrMethodName>
```

Never pass `--all` on the agent's own initiative.

## Notes and limits

- `generate` **executes** the target methods to record what they return. Methods tagged `io` or `money`
  are skipped by default for that reason. Do not pass `--allow-side-effects` without explicit user
  approval: it can delete data, send email, or charge a card.
- Locking is worthwhile for pure and near-pure logic — calculations, parsing, formatting, validation,
  branching on inputs. It is not useful for thin controllers or code that is mostly I/O.
- If a method cannot be locked because it reads ambient state (`DateTime.Now`, `Guid.NewGuid`),
  `pinion seam <path>` can introduce a test seam so it becomes lockable. Preview first; it is
  preview-by-default and compile-gated.
- On a large solution, scope the post-upgrade check to what changed:
  `pinion verify <test.csproj> --since HEAD~1`
- Generated tests and snapshots live under `<test-project>/PinionCharacterization/`. They are ordinary
  xUnit tests and run in normal `dotnet test` runs.
