# Pullback

NinjaTrader 8 strategy: trades pullbacks to a **self-calibrating moving
average**. Instead of a fixed period, a deterministic engine scores every
candidate MA (SMA/EMA, periods 8-60) by how many *confirmed bounces* price
gave it over the last few hours (failures subtract, recent events weigh
more) and trades the winner, with hysteresis so the selection doesn't
ping-pong.

- Instrument/timeframe: NQ/MNQ, 1-minute.
- Entry: pullback touch of the active MA + rejection bar closing back on
  the trend side. Trend = active-MA slope + EMA(200) context filter.
- Exit: structural stop under the pullback swing (6-tick floor), target at
  1.5R. Session window, max trades/day, and max daily loss guardrails.
- No ML — the engine is bounce counting with decay. Design doc:
  [`docs/2026-07-20-pullback-design.md`](docs/2026-07-20-pullback-design.md).

## Files

| Path | Purpose |
|------|---------|
| `Pullback.cs` | The strategy (single file, managed orders) |
| `scripts/sync-to-nt8.sh` | Copies the .cs into `Documents/NinjaTrader 8/bin/Custom/Strategies/` |
| `docs/` | Design spec, implementation plan, validation results |

## Dev setup (per machine)

1. Clone, then install the sync hook:
   `printf '#!/bin/sh\nexec scripts/sync-to-nt8.sh\n' > .git/hooks/post-commit && chmod +x .git/hooks/post-commit`
2. Every commit copies `Pullback.cs` to the NT8 Strategies folder; press F5
   in the NinjaScript Editor to recompile.
3. Out-of-editor compile check: `nt8c check Pullback.cs`.
