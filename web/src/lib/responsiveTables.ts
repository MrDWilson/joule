/**
 * Narrow screens show each table row as a labelled card (see `.table-wrap` in styles.css).
 * The card labels come from the column headings, copied onto every body cell as `data-label`,
 * so individual tables do not have to repeat their headings in markup.
 */
export function labelTableCells(root: ParentNode = document) {
  for (const table of Array.from(root.querySelectorAll<HTMLTableElement>(".table-wrap table"))) {
    const headings = Array.from(table.tHead?.rows[0]?.cells ?? []).map((cell) => cell.textContent?.trim() ?? "");
    for (const body of Array.from(table.tBodies))
      for (const row of Array.from(body.rows))
        Array.from(row.cells).forEach((cell, index) => {
          const label = headings[index];
          if (label && cell.dataset.label !== label) cell.dataset.label = label;
        });
  }
}

/** Keeps labels current as React renders tables; attribute writes do not retrigger the observer. */
export function observeTableLabels() {
  let queued = false;
  const run = () => {
    queued = false;
    labelTableCells();
  };
  const observer = new MutationObserver(() => {
    if (queued) return;
    queued = true;
    requestAnimationFrame(run);
  });
  observer.observe(document.body, { childList: true, subtree: true, characterData: true });
  run();
  return () => observer.disconnect();
}
