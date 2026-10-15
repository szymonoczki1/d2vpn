# Session logs

Raw output from individual measurement runs, kept because the numbers in `docs/protocol.md` and in
the measured defaults are claims, and a claim without its working is just an assertion.

Nothing here is read by the application. They are tab-separated event logs written by
`d2vpn hold --log`, each one a single run: when the hold started, what crossed, when it was
released. No addresses appear in them - the captures those came from are not in this repository
and never will be, for the reason given in `.gitignore`.

| file | what it is |
|---|---|
| `attempts.tsv`, `attempts2.tsv`, `attempts3.tsv` | early 3074 holds, finding the ceiling a hold dies at |
| `wide.tsv`, `trials.tsv` | pass-list candidates across several runs |
| `longhold-451s.tsv` | a 451 s hold, which is where the resync stopped being a hypothesis |
| `longhold-295s-buffered.tsv` | the same with buffering on, for the replay-on-release question |
| `30kslow-244s.tsv` | 30k under `--slow`, the rate cap rather than the hold |
| `api-7500-44s.tsv`, `api-7500-ceiling.tsv`, `api-7500-replay.tsv` | 7500, its ceiling, and what a replay cost |
| `rc-first-run.tsv` | the first reconnect that worked |
