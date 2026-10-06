import { useState } from "react";
import { Battery, Home, Lightbulb, RefreshCw, Sun, Wallet } from "lucide-react";
import {
  Button,
  ButtonLink,
  Card,
  Chip,
  Disclosure,
  EmptyState,
  ErrorNotice,
  LoadingBlock,
  Modal,
  Segmented,
  Stat,
  Switch,
  preview,
} from "../components/ui";
import { Hint } from "../components/Hint";
import { gbp, kwh, pence, percent } from "../lib/format";
import { dayTime, range } from "../lib/time";
import { ChartShowcase } from "../components/charts/ChartShowcase";

const swatches = [
  ["--bg-0", "Page"],
  ["--bg-1", "Panel"],
  ["--bg-2", "Raised"],
  ["--bg-3", "Pressed"],
  ["--text-1", "Text"],
  ["--text-2", "Secondary"],
  ["--text-3", "Muted"],
  ["--accent", "Accent"],
  ["--load", "Home use"],
  ["--solar", "Solar"],
  ["--battery", "Battery"],
  ["--ev", "Car"],
  ["--export", "Export"],
  ["--warn", "Warn"],
  ["--danger", "Danger"],
  ["--info", "Info"],
] as const;

/** #/kit (development builds only): every interface primitive in every state, for design review. */
export default function KitPage() {
  const [segment, setSegment] = useState<"off" | "yesterday" | "week">("yesterday");
  const [on, setOn] = useState(true);
  const [dialog, setDialog] = useState(false);
  const longTitle =
    "The overnight charge stopped at 87% because the cheap window ended before the target was reached, and the morning peak then drew from the grid";
  return (
    <div className="kit">
      <section aria-labelledby="kit-colour">
        <h2 id="kit-colour">Colour</h2>
        <div className="kit-grid">
          {swatches.map(([token, name]) => (
            <span key={token} className="kit-swatch">
              <i style={{ background: `var(${token})` }} />
              {name} <code>{token}</code>
            </span>
          ))}
        </div>
      </section>

      <section aria-labelledby="kit-type">
        <h2 id="kit-type">Type</h2>
        <p style={{ fontSize: "var(--fs-display)", fontWeight: 600, letterSpacing: "-0.02em" }}>£1.10</p>
        <p style={{ fontSize: "var(--fs-2xl)", fontWeight: 650 }}>Page title 28</p>
        <p style={{ fontSize: "var(--fs-xl)", fontWeight: 600 }}>Section heading 20</p>
        <p style={{ fontSize: "var(--fs-lg)", fontWeight: 600 }}>Card title 16</p>
        <p className="body-copy">Body 14. Predbat charged the battery to 100% overnight at 6.7p/kWh.</p>
        <p className="muted">Secondary 13. Today so far, from midnight.</p>
        <p style={{ fontSize: "var(--fs-xs)", color: "var(--text-3)" }}>Meta 12, the floor.</p>
      </section>

      <section aria-labelledby="kit-buttons">
        <h2 id="kit-buttons">Buttons</h2>
        {(["primary", "secondary", "ghost", "link", "danger"] as const).map((variant) => (
          <div className="kit-row" key={variant}>
            {(["sm", "md", "lg"] as const).map((size) => (
              <Button key={size} variant={variant} size={size}>
                {variant} {size}
              </Button>
            ))}
            <Button variant={variant} disabled>
              Disabled
            </Button>
            <Button variant={variant}>
              <RefreshCw size={15} aria-hidden="true" /> With icon
            </Button>
          </div>
        ))}
        <div className="kit-row">
          <ButtonLink href="#/plan">Link styled as a button</ButtonLink>
          <a className="text-link" href="#/plan">
            View full plan
          </a>
        </div>
      </section>

      <section aria-labelledby="kit-chips">
        <h2 id="kit-chips">Chips</h2>
        <div className="kit-row">
          {(["neutral", "info", "success", "warn", "danger", "violet"] as const).map((tone) => (
            <Chip key={tone} tone={tone}>
              {tone === "neutral"
                ? "Estimate"
                : tone === "info"
                  ? "Running"
                  : tone === "success"
                    ? "Ready for review"
                    : tone === "warn"
                      ? "≈ estimated"
                      : tone === "danger"
                        ? "Sensor offline"
                        : "Hold battery"}
            </Chip>
          ))}
          <Chip tone="success" dot>
            Connected
          </Chip>
          <Chip tone="warn" dot>
            Missing 00:00–02:47
          </Chip>
        </div>
      </section>

      <section aria-labelledby="kit-controls">
        <h2 id="kit-controls">Controls</h2>
        <div className="kit-row">
          <Segmented
            label="Compare with"
            value={segment}
            onChange={setSegment}
            options={[
              { value: "off", label: "Off" },
              { value: "yesterday", label: "Yesterday" },
              { value: "week", label: "Last week" },
            ]}
          />
          <Segmented
            label="Range (small)"
            size="sm"
            value={segment}
            onChange={setSegment}
            options={[
              { value: "off", label: "12 h" },
              { value: "yesterday", label: "24 h" },
              { value: "week", label: "48 h", disabled: true },
            ]}
          />
          <label className="select-label">
            <Switch checked={on} onCheckedChange={setOn} label="Automatic checks" /> Automatic checks
          </label>
          <Switch checked={false} onCheckedChange={() => {}} label="Disabled switch" disabled />
          <span>
            Export freeze
            <Hint label="What does export freeze mean?">
              The battery won't charge, so spare solar is exported; the battery still covers the house if solar falls
              short.
            </Hint>
          </span>
        </div>
        <label className="field">
          A text field
          <input placeholder="Search settings…" />
        </label>
        <label className="field">
          A choice
          <select defaultValue="b">
            <option value="a">Watch only</option>
            <option value="b">Suggest changes</option>
          </select>
        </label>
      </section>

      <section aria-labelledby="kit-stats">
        <h2 id="kit-stats">Stat tiles</h2>
        <div className="kit-grid">
          <Stat
            label="Net cost today"
            value={gbp(1.1)}
            icon={<Wallet />}
            delta="Paid £1.80 · Earned £0.70"
            accent="accent"
          />
          <Stat
            label="Solar"
            value={kwh(9.47, { unit: false })}
            unit="kWh"
            icon={<Sun />}
            accent="solar"
            delta="Yesterday by now: 7.1 kWh"
            status={<Chip tone="warn">Night counted as 0</Chip>}
          />
          <Stat
            label="Home use"
            value={kwh(12.25, { unit: false })}
            unit="kWh"
            icon={<Home />}
            accent="load"
            status={<Chip tone="danger">Sensor offline 10:00–11:30</Chip>}
          />
          <Stat
            label="Battery"
            value={percent(74)}
            icon={<Battery />}
            accent="battery"
            footnote={`as of ${dayTime(Date.now() - 600000)}`}
          />
          <Stat label="Import price" value={pence(24.123456, { unit: "p" })} unit="/kWh" accent="neutral" size="sm" />
        </div>
      </section>

      <section aria-labelledby="kit-cards">
        <h2 id="kit-cards">Cards</h2>
        <div className="card-grid">
          <Card
            title={longTitle}
            eyebrow={<Chip tone="warn">Problem</Chip>}
            footer={
              <>
                <Button size="sm">Review</Button>
                <Button size="sm" variant="ghost">
                  Not now
                </Button>
              </>
            }
          >
            <p className="body-copy">
              {preview("Predbat planned to charge to 100% between 23:30 and 05:30 at 6.7p/kWh. ".repeat(6))}
            </p>
          </Card>
          <Card title="Charge window" eyebrow={<Chip tone="info">Plan</Chip>}>
            <p className="body-copy">{range(Date.now() + 3600000, Date.now() + 4 * 3600000)}</p>
          </Card>
        </div>
      </section>

      <section aria-labelledby="kit-disclosure">
        <h2 id="kit-disclosure">Disclosure</h2>
        <Disclosure summary="Evidence" count={3}>
          <p>Three measured facts the check relied on.</p>
        </Disclosure>
        <Disclosure summary="How the figures are worked out" defaultOpen>
          <p>Readings every 5 minutes; offline time is shown as missing, never guessed.</p>
        </Disclosure>
      </section>

      <section aria-labelledby="kit-states">
        <h2 id="kit-states">Empty, loading and error</h2>
        <div className="kit-grid">
          <EmptyState
            title="Nothing waiting for you"
            icon={<Lightbulb size={28} />}
            action={<Button variant="secondary">Run check</Button>}
          >
            Suggestions appear here when a check finds a setting worth changing.
          </EmptyState>
          <EmptyState title="No plan yet" />
          <div>
            <EmptyState quiet title="Nothing to review">
              last check 10:25
            </EmptyState>
            <LoadingBlock lines={3} />
            <LoadingBlock height={80} label="Loading chart" />
          </div>
          <ErrorNotice error={new TypeError("Failed to fetch")} onRetry={() => {}} title="Couldn't load the plan" />
        </div>
      </section>

      <section aria-labelledby="kit-dialog">
        <h2 id="kit-dialog">Dialog</h2>
        <div className="kit-row">
          <Button variant="secondary" onClick={() => setDialog(true)}>
            Open a dialog
          </Button>
        </div>
        <Modal
          open={dialog}
          onOpenChange={setDialog}
          title="Review suggestion"
          description="See exactly what would change before you decide."
          footer={
            <>
              <Button variant="ghost" onClick={() => setDialog(false)}>
                Not now
              </Button>
              <Button onClick={() => setDialog(false)}>Apply change</Button>
            </>
          }
        >
          {Array.from({ length: 8 }, (_, i) => (
            <p key={i} className="body-copy">
              Battery max charge rate 100% → 67%. Paragraph {i + 1} of a long explanation, so the sticky footer can be
              seen on a phone.
            </p>
          ))}
        </Modal>
      </section>

      <section aria-labelledby="kit-charts">
        <h2 id="kit-charts">Charts</h2>
        <ChartShowcase />
      </section>
    </div>
  );
}
