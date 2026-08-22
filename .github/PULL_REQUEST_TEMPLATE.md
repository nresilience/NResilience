<!--
Thanks for the PR. A few things to check before asking for review.

The CI gates that run on your PR are the same ones that gate a release, so a
green build is a strong signal. None of the items below are hard requirements
to open the PR - they are a checklist for getting it merged.
-->

## What

<!-- One or two sentences on what this changes and why. -->

## Checklist

- [ ] The build is green locally: `dotnet test tests/NResilience.Tests -c Release` and `dotnet test tests/NResilience.Gates -c Release`.
- [ ] If the public API changed, `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` were updated and the diff is intentional.
- [ ] If docs are affected, the snippets under `tests/NResilience.Docs` still compile and `dotnet run --project tools/NResilience.DocSnippets -- --check` passes.
- [ ] If an analyzer rule changed, `AnalyzerReleases.Shipped.md` / `AnalyzerReleases.Unshipped.md` were updated.
- [ ] No new analyzer suppression landed in a docs snippet without a reason recorded in the PR.

## Notes for the reviewer

<!-- Anything you want to draw attention to - design tradeoffs, alternatives
considered, anything that is deliberately rough and will be cleaned up later. -->