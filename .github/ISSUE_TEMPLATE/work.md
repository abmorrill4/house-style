---
name: Work item
about: A fix, feature or larger piece of work, tracked through the work loop
labels: []
---

<!-- This repo is public. A vulnerability with an exploit path goes to a private security advisory, not here.
     Never paste tester report text or names. Add one origin label: from:tester, from:owner or from:research.
     Add an area:* label (rules/areas.json) and set When on the board. -->

**Size:** Fix | Feature | Large

## Intent
What changes for a writer, where it came from (the origin label, no names), why now, and what is not included.
For a Fix, name the test that reproduces the bug.

## Research
<!-- Feature and Large. Findings, with links to planning/work/<issue>-<slug>/research.md -->

## Spec and verification plan
<!-- Behavior, copy, and the tests and real-Flare checks that will prove it works. -->

## Stages
- [ ] Intent
- [ ] Research (Feature, Large)
- [ ] Spec and verification plan (Feature, Large)
- [ ] Spec review: adversarial, consistency, security, goals (Feature, Large)
- [ ] Build
- [ ] Verify
- [ ] Build review: correctness, security, consistency
- [ ] Ship
