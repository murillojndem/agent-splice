import { describe, expect, it } from 'vitest';
import styles from '../src/styles.css?raw';

// Colour carries meaning on the pills, and jsdom computes no colours, so the rule is read from the
// stylesheet itself (ADR 0026).

/** The declarations of every rule whose selector list mentions the class, combined selectors included. */
function rulesFor(className: string): string[] {
  const mention = new RegExp(`\\.${className}(?![\\w-])`);

  return [...styles.matchAll(/([^{}]+)\{([^}]*)\}/g)]
    .filter(([, selectors]) => mention.test(selectors ?? ''))
    .map(([, , declarations]) => declarations ?? '');
}

/** The literal a custom property is defined as, so a rule that spells the colour out is caught too. */
function valueOf(property: string): string {
  const defined = new RegExp(`${property}:\\s*([^;]+);`).exec(styles);

  expect(defined).not.toBeNull();

  return (defined?.[1] ?? '').trim().toLowerCase();
}

describe('the colour of an outcome', () => {
  it('does not paint a completed exchange as a success', () => {
    // A runtime that answered 500, or reported an error inside a 200, completed too.
    const completed = rulesFor('pill-completed').join('').toLowerCase();

    expect(completed.length).toBeGreaterThan(0);
    expect(completed).not.toContain('var(--good)');
    expect(completed).not.toContain(valueOf('--good'));
  });

  it('paints a runtime success good and a runtime error bad', () => {
    expect(rulesFor('pill-success').join('')).toContain('var(--good)');
    expect(rulesFor('pill-error_status').join('')).toContain('var(--bad)');
    expect(rulesFor('pill-error_reported').join('')).toContain('var(--bad)');
  });
});
