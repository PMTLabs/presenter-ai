import { describe, expect, it } from 'vitest';
import { isProblem } from './problem';
import type { ErrorCode } from './errorCodes';

describe('Problem Details narrowing', () => {
  it('narrows a Problem Details body to a known error code', () => {
    const body: unknown = { type: 'about:blank', title: 'Busy', status: 429, code: 'session.slots_busy' };
    expect(isProblem(body)).toBe(true);
    if (isProblem(body)) {
      const code: ErrorCode = body.code;
      expect(code).toBe('session.slots_busy');
    }
  });

  it('rejects non-problem JSON', () => {
    expect(isProblem({ error: 'not a problem', message: 'legacy' })).toBe(false);
    expect(isProblem({ code: 'unknown.code', title: 'Unknown' })).toBe(false);
  });
});
