#!/bin/bash
# Script to push all phase branches for infraAndTestingVNext
# Run this script after the plan branch has been created locally

set -e

cd "$(dirname "$0")/../.."

echo "Setting up branch structure for infraAndTestingVNext..."

# Ensure we're on the plan branch
git checkout plan/infraAndTestingVNext || {
    echo "Creating plan branch from current location..."
    git checkout -b plan/infraAndTestingVNext
}

# Create phase branches if they don't exist
for phase in phase0 phase1 phase2 phase3 phase4; do
    branch_name="plan/infraAndTestingVNext-${phase}"
    if git rev-parse --verify "$branch_name" >/dev/null 2>&1; then
        echo "Branch $branch_name already exists"
    else
        echo "Creating branch $branch_name"
        git checkout -b "$branch_name" plan/infraAndTestingVNext
        git checkout plan/infraAndTestingVNext
    fi
done

echo ""
echo "Branch structure created. Local branches:"
git branch | grep "plan/"

echo ""
echo "To push all branches to remote, run:"
echo "  git push -u origin plan/infraAndTestingVNext"
echo "  git push -u origin plan/infraAndTestingVNext-phase0"
echo "  git push -u origin plan/infraAndTestingVNext-phase1"
echo "  git push -u origin plan/infraAndTestingVNext-phase2"
echo "  git push -u origin plan/infraAndTestingVNext-phase3"
echo "  git push -u origin plan/infraAndTestingVNext-phase4"
