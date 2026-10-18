import { describe, expect, it } from 'vitest';
import styles from '../src/styles.css?raw';

// The one place colour carries meaning is the pills, and jsdom computes no colours, so the rule is read
// from the stylesheet itself (ADR 0026).

/** The declarations of every rule whose selector list names the class. */
function rulesFor(className: string): string[] {
  const rules = [...styles.matchAll(/([^{}]+)\{([^}]*)\}/g)];

  return rules
    .filter(([, selectors]) => (selectors ?? '').split(',').some((selector: string) => selector.trim() === `.${className}`))
    .map(([, , declarations]) => declarations ?? '');
}

describe('the colour of an outcome', () => {
  it('does not paint a completed exchange as a success', () => {
    // A runtime that answered 500, or reported an error inside a 200, completed too.
    const completed = rulesFor('pill-completed');

    expect(completed.length).toBeGreaterThan(0);
    expect(completed.join('')).not.toContain('var(--good)');
  });

  it('paints a runtime success good and a runtime error bad', () => {
    expect(rulesFor('pill-success').join('')).toContain('var(--good)');
    expect(rulesFor('pill-error_status').join('')).toContain('var(--bad)');
    expect(rulesFor('pill-error_reported').join('')).toContain('var(--bad)');
  });
});
