# Quick Start Guide for infraAndTestingVNext

This guide helps you get started with working on the infrastructure and testing modernization project.

## Overview

The modernization plan is organized into 5 phases, each with its own branch:
- **Phase 0**: Planning & Documentation Skeleton
- **Phase 1**: TestSupport Library and Shared Identities
- **Phase 2**: Docker Compose Dev Stack & Infra Scripts
- **Phase 3**: Replace Aspire, Move from SQLite to PostgreSQL, and Clean Docs
- **Phase 4**: HVO.SkyMonitor Integration Tests (HTTP-only, Testcontainers)

## Getting Started

### 1. Set Up the Branch Structure

If the phase branches haven't been pushed to remote yet, create them locally:

```bash
./docs/projects/setup-branches.sh
```

### 2. Choose a Phase to Work On

Check out the phase branch:

```bash
git checkout plan/infraAndTestingVNext-phase0  # For Phase 0
# Or any other phase: phase1, phase2, phase3, phase4
```

### 3. Review the Plan

Open [plan-infraAndTestingVNext.prompt.md](plan-infraAndTestingVNext.prompt.md) and find your phase section. Each phase has detailed checklist items describing the work to be done.

### 4. Create a Feature Branch (Optional)

For larger changes within a phase, create a feature branch:

```bash
git checkout -b feature/phase0-my-feature plan/infraAndTestingVNext-phase0
```

### 5. Make Your Changes

Follow the repository's coding guidelines in `.github/copilot-instructions.md`:
- Use .NET 10 features
- Follow the project structure conventions
- Write tests for new functionality
- Document your changes

### 6. Commit and Push

Commit your changes to the phase branch:

```bash
git add .
git commit -m "Phase 0: Add plan document"
git push origin plan/infraAndTestingVNext-phase0
```

## Phase Dependencies

Be aware of these dependencies when working on phases:

- **Phase 0** should be completed first (establishes conventions)
- **Phase 1** can be worked on independently after Phase 0
- **Phase 2** can be worked on in parallel with Phase 1
- **Phase 3** should wait for Phase 2 (infrastructure dependencies)
- **Phase 4** depends on both Phase 1 (TestSupport) and Phase 2 (infrastructure)

## Merging Phases

When a phase is complete and tested:

1. Ensure all checklist items are marked complete
2. Get the phase branch reviewed
3. Merge to the main plan branch:
   ```bash
   git checkout plan/infraAndTestingVNext
   git merge plan/infraAndTestingVNext-phase0
   git push origin plan/infraAndTestingVNext
   ```

## Resources

- **Full Plan**: [plan-infraAndTestingVNext.prompt.md](plan-infraAndTestingVNext.prompt.md)
- **Branch Structure**: [BRANCH_STRUCTURE.md](BRANCH_STRUCTURE.md)
- **Coding Guidelines**: `.github/copilot-instructions.md`
- **Setup Script**: [setup-branches.sh](setup-branches.sh)

## Questions?

If you have questions about the plan or need clarification on any phase, please open an issue or discussion in the repository.
