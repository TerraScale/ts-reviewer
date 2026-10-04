AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!

# Rules
- Never add AI attribution to commits, PR titles, or PR descriptions: no `Co-Authored-By:` trailers, no `🤖 Generated with [Claude Code]` or similar generated-by notices.
- This project needs to use C#
- This needs to be deployed using the dotnet container way of doing things
- The project must follow a feature sliced design so it is well organized and similar or dependant files and services are kept together

# Libraries

## Local CPU Priority

- When building or testing locally, run every build, test, lint, and benchmark
  command at low CPU priority by prefixing it with `nice -n 19` (for example
  `nice -n 19 make test-fast`). Local dev servers and watchers count too.
- In CI (`CI=true`) use full CPU priority: do nothing. Never add `nice` or
  `renice` to CI workflows, Makefiles, scripts, or Containerfiles; the low
  priority is applied by whoever runs the command locally.

## Tests
- We MUST use TUnit for tests
- Prefer using `dotnet run --project {project}` instead of `dotnet test` as using dotnet run for TUnit is faster
