# Pinion + GitHub Copilot upgrade agent

Microsoft's [Copilot upgrade agent](https://learn.microsoft.com/en-us/dotnet/core/porting/github-copilot-upgrade/overview)
transforms your .NET code. It validates the result by checking that your **build and tests pass** —
which proves almost nothing on a codebase without tests, and those are the codebases that most need
upgrading.

This directory holds a **custom upgrade instruction** that closes the gap: the agent locks current
behavior with Pinion before it changes anything, then proves after the upgrade that behavior is
unchanged.

```
pinion analyze   →  pinion quickstart  →  [Copilot upgrades]  →  pinion verify
where am I           lock what the                                 identical, or
exposed?             code does today                               exactly what changed
```

Pinion's default generator is deterministic and local, so this step sends nothing anywhere.

## Install

Copy the instruction file into the repository being upgraded:

```pwsh
# from the root of the repo you are upgrading
mkdir -Force .github/upgrades
curl -o .github/upgrades/lock_behavior_before_upgrading.md `
  https://raw.githubusercontent.com/AGomnes/Pinion/main/integrations/github-copilot-upgrade/lock_behavior_before_upgrading.md
```

Custom upgrade instructions are per-repository — the agent reads them from the repo it is working in,
so this has to be copied into each one.

Then make sure Pinion itself is available:

```pwsh
dotnet tool install -g Pinion
```

## Use

Start the agent as usual (`@upgrade` in VS Code or Copilot CLI, **Modernize** in Visual Studio), and
during the **assessment** stage ask for the instruction by name:

> use the custom instructions to lock behavior before upgrading

Microsoft's guidance is that activation is more reliable when your wording matches the file name, so
lead with the verb: *"lock behavior before upgrading"*, not *"use Pinion"*. Requesting it during
assessment works better than waiting until planning or execution.

Confirm in the chat that the agent says it retrieved the instruction file. If it doesn't, restate the
request using the file name's key words.

To make it stick across sessions, tell the agent:

> From now on, always lock behavior with Pinion before upgrading any project.

That preference is written to `.github/upgrades/scenario-instructions.md` and persists.

## What is proven, and what is not

**Proven:** the commands in the instruction file are the real Pinion CLI surface, verified against
`pinion --help`. Run by hand, the workflow works — that is the same `analyze → generate → verify` loop
demonstrated on nopCommerce in [PROOF.md](../../PROOF.md).

**Not proven:** whether the Copilot agent reliably *executes* an external CLI as part of an upgrade
plan. Microsoft documents custom instructions as automating code and dependency changes, and publishes
no example of shelling out to a third-party tool. It may work well; we have not verified it, and this
file does not claim otherwise.

If the agent describes the steps but does not run them, the instruction is still useful — it tells you
exactly which commands to run and when, and the ordering (lock before, verify after) is the part that
matters. Reports either way are welcome in
[issues](https://github.com/AGomnes/Pinion/issues).

## The failure mode this guards against

An agent that sees `pinion verify` fail and "fixes" it by running `pinion accept`. That re-baselines
the golden master to whatever the upgraded code now does, marking the changed behavior as correct and
erasing the evidence. The instruction file forbids this explicitly, and it is the single most important
line in it.
