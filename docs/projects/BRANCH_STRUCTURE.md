# Branch Structure for infraAndTestingVNext Implementation

This document describes the branch structure created for implementing the phased modernization plan outlined in `plan-infraAndTestingVNext.prompt.md`.

## Branch Hierarchy

```
plan/infraAndTestingVNext (main plan branch)
├── plan/infraAndTestingVNext-phase0 (Planning & Documentation Skeleton)
├── plan/infraAndTestingVNext-phase1 (TestSupport Library and Shared Identities)
├── plan/infraAndTestingVNext-phase2 (Docker Compose Dev Stack & Infra Scripts)
├── plan/infraAndTestingVNext-phase3 (Replace Aspire, Move from SQLite to PostgreSQL, and Clean Docs)
└── plan/infraAndTestingVNext-phase4 (HVO.SkyMonitor Integration Tests)
```

## Workflow

1. **Main Plan Branch**: `plan/infraAndTestingVNext`
   - Contains the plan document and serves as the base for all phase branches
   - Changes that affect multiple phases or the overall plan should be made here

2. **Phase Branches**: Each phase has its own branch for focused development
   - **Phase 0** (`plan/infraAndTestingVNext-phase0`): Planning & Documentation Skeleton
     - Create master plan document
     - Establish naming and structure conventions
   
   - **Phase 1** (`plan/infraAndTestingVNext-phase1`): TestSupport Library and Shared Identities
     - Create HVO.SkyMonitor.TestSupport project
     - Define shared test constants and utilities
   
   - **Phase 2** (`plan/infraAndTestingVNext-phase2`): Docker Compose Dev Stack & Infra Scripts
     - Add docker-compose.dev.yml
     - Implement infra scripts for service management
   
   - **Phase 3** (`plan/infraAndTestingVNext-phase3`): Replace Aspire, Move from SQLite to PostgreSQL
     - Replace SQLite with PostgreSQL
     - Remove Aspire dependencies
     - Update documentation
   
   - **Phase 4** (`plan/infraAndTestingVNext-phase4`): HVO.SkyMonitor Integration Tests
     - Create integration tests project
     - Implement Testcontainers-based test fixtures
     - Add comprehensive test suites

## Usage

### Starting Work on a Phase

```bash
# Switch to the phase branch you want to work on
git checkout plan/infraAndTestingVNext-phase0

# Create a feature branch from the phase branch (optional)
git checkout -b feature/phase0-plan-document

# Make your changes and commit
# ...

# When ready, merge back to the phase branch
git checkout plan/infraAndTestingVNext-phase0
git merge feature/phase0-plan-document
```

### Merging Phase Work

When a phase is complete, it can be merged to the main plan branch:

```bash
git checkout plan/infraAndTestingVNext
git merge plan/infraAndTestingVNext-phase0
```

Eventually, when all phases are complete and tested, the plan branch can be merged to the main development branch.

## Notes

- Each phase branch starts from the same point as the main plan branch
- Phases can be worked on in parallel, but dependencies should be considered
- Phase 0 should be completed first as it establishes conventions
- Phase 3 has significant infrastructure changes that may affect other phases
- Phase 4 depends on infrastructure from Phase 2 and support from Phase 1
