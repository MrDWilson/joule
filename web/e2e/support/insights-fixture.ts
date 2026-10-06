/**
 * A live-shaped Insights state for the demo payload: about three days of hourly automatic checks (mostly quiet, some
 * problems with to-dos and file edits, a handful that didn't finish), quick checks without the AI, ChatGPT usage, and
 * closed follow-ups. Synthetic text modelled on the live install's records (no real identifiers).
 */
type Json = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any

const problems = [
  {
    title: 'Charge plan reversed for 20 minutes, exporting 2.78 kWh in a cheap slot',
    summary:
      'During the 03:00–03:30 cheap slot Predbat switched from charging to export for 20 minutes, ending at 31% against 55% planned. The next FrzExp slot held the battery level as expected.',
  },
  {
    title: 'Solar validation failed 7 times, then Predbat stopped retrying',
    summary: 'Predbat failed `pv_today` validation seven times from 04:30 local, then stopped retrying until a restart.',
  },
  {
    title: 'Two energy sensors became non-numeric, blocking export and PV verification',
    summary: 'Battery control continued charging correctly, but `sensor.my_home_solar_generated` read unknown after midnight.',
  },
  {
    title: 'Late smart slot left battery at 78% against a 100% target',
    summary: 'A cheap Intelligent Octopus slot arrived nine minutes after it began, so the frozen plan still showed Demand slots.',
  },
];

export function liveShaped(payload: Json, now = Date.now()): Json {
  const base = payload.state.investigations[0];
  const hour = 3600_000;
  const investigations: Json[] = [];
  const usage: Json[] = [];
  const activities: Json[] = [];
  for (let n = 66; n >= 1; n--) {
    const at = new Date(now - n * hour - 20 * 60_000).toISOString();
    const id = `live-${n}`;
    const failed = n % 9 === 4;
    const problem = !failed && n % 7 === 2;
    const p = problems[n % problems.length];
    investigations.push({
      ...base,
      id,
      at,
      provider: 'ChatGpt',
      status: failed ? 'Failed' : 'Completed',
      verdict: failed ? null : problem ? 'problem' : 'no_change',
      failureKind: failed ? 'provider_busy' : null,
      title: failed ? "Check didn't finish" : problem ? p.title : 'No material change since the last review',
      headline: null,
      plain: null,
      summary: failed
        ? 'ChatGPT response failed (error). Analysis was discarded; no paid API fallback was attempted.'
        : problem
          ? p.summary
          : 'No new cost, control or telemetry problem appeared, and no cheap-import window completed.',
      evidence: problem ? ['At 03:20 local the battery exported 2.78 kWh while import cost 6.67p/kWh.'] : [],
      steps: ['plan_vs_actual: 2026-10-05T04:00:00.0000000+01:00/2026-10-05T08:18:00.0000000+01:00 (retrieved; evidence tool-1)'],
      stepDetails: [],
      request: { question: 'Scheduled review of the period since the last review… (1) battery outcome versus plan…', from: null, to: null, scheduled: true },
      nextSteps:
        problem && n < 30
          ? [
              {
                id: `step-${n}`,
                title: `Restore the solar sensor reading ${n}`,
                rationale: 'The solar meter read unknown.',
                suggestedAction: 'Check `sensor.my_home_solar_generated` reports a number after sunrise.',
                verification: 'Watch the next morning.',
                uncertainty: 'None.',
                evidenceReferences: [],
                status: n < 12 ? 'open' : 'closed',
                closedReason: n < 12 ? null : 'No longer carried forward by the investigation at 2026-10-04 12:50:27Z',
                closedAt: n < 12 ? null : at,
                thread: [],
              },
            ]
          : [],
      fileChanges: [],
      thread: [],
      toolEvidence: [],
      evidenceReferences: [],
      dismissedAt: null,
      impactPence: problem ? 45 : null,
      occurrences: 1,
    });
    usage.push({
      at: new Date(Date.parse(at) + 4 * 60_000).toISOString(),
      provider: 'ChatGpt',
      model: 'gpt-5.6-sol',
      inputTokens: failed ? 18_000 : 180_000,
      outputTokens: failed ? 400 : 2_200,
      estimatedUsd: null,
      status: failed ? 'Failed' : 'Completed',
    });
    activities.push({
      at: new Date(Date.parse(at) + 30 * 60_000).toISOString(),
      kind: 'check',
      message: 'Checked 15:40–16:41: nothing new. Battery 85% at 16:00 against 85% planned. No new Predbat warnings. The AI wasn\'t needed.',
    });
  }
  payload.state.investigations = investigations;
  payload.state.usage = usage;
  payload.state.activities = activities;
  payload.state.ai = { ...payload.state.ai, provider: 'ChatGpt', model: 'gpt-5.6-sol', scheduled: true };
  payload.ai.chatGptConnected = true;
  payload.ai.running = false;
  return payload;
}
