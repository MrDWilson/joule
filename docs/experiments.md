# Changes and trials

## Control modes

| Mode | What Joule does |
| --- | --- |
| **Monitor** | Watches and reviews. Makes no suggestions to change settings and never writes. |
| **Recommend** | Suggests exact changes with evidence. You approve each one. |
| **Auto** | Applies only small changes to settings you have explicitly allowed, within limits you set. Everything else still needs approval. |

Live writes also need `Predbat__WritesEnabled=true`. Without it, a suggestion tells you the value to set in Predbat yourself.

**Approve once** applies a suggestion without allowing future changes. **Approve and allow future changes** also gives permission for that setting. You can withdraw permission at any time. In Auto mode a change must match the current value and revision, stay within the minimum and maximum you set, be no larger than the per-setting step (at most 0.10), respect a cooldown, and only touch low-risk tunable settings. Predbat's own manual overrides, software updates and control switches are never tunable.

## History and undo

Every change records the full set of tunable settings, when, who or what made it, why, and the difference. You can undo a single change, restore an earlier version, or download a snapshot. A restore creates a new version rather than rewriting history. Undo refuses to overwrite later changes to the same setting and leaves other settings alone. If Predbat's settings change outside Joule, that is recorded too.

## Trials

Each change starts a trial: Joule compares equal-length periods before and after (up to seven days each) on forecast accuracy and on measured net cost. It checks for things that would muddy the comparison, such as missing data, different tariffs, much more solar, car charging or further setting changes. You can keep, extend, close or undo a trial and note why. Trials live under Insights › Trials.

Before-and-after comparisons are observations, not proof. Joule only calls a result clear when both periods are well measured (at least 99% of cost data, no more than 30 minutes missing) and comparable.

Automatic undo exists only for single load or solar scaling changes in Auto mode, after at least three days and 48 matched half-hours, when forecast accuracy got more than 25% worse **and** measured daily net cost got worse by more than 10p or 10%. A worse forecast alone never triggers an undo while costs improved.

## Previews

For load and solar scaling changes, the review dialog shows a simple preview: Predbat's captured forecast against the adjusted one, and how much import and export exposure that shifts. It is not a Predbat replan: battery schedules and charge windows are not recalculated, and changes without a meaningful preview get none.
