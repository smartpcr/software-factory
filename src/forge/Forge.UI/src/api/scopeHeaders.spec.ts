// Phase 80.G — pure-logic tests for the request scope-header builder.
// `buildScopeHeaders` decides which of `X-Forge-Product-Id` / `X-Forge-Repo-Id`
// rides on every request; it must omit an id that is null/empty so the server
// treats it as product-wide / all-repos.

import { describe, expect, it } from 'vitest';
import { buildScopeHeaders } from './forgeApi';

describe('buildScopeHeaders', () => {
  it('emits both headers when product and repo are set', () => {
    expect(buildScopeHeaders('atlas', 'atlas-platform')).toEqual({
      'X-Forge-Product-Id': 'atlas',
      'X-Forge-Repo-Id': 'atlas-platform',
    });
  });

  it('emits only the product header when no repo is selected (all-repos)', () => {
    expect(buildScopeHeaders('atlas', null)).toEqual({
      'X-Forge-Product-Id': 'atlas',
    });
  });

  it('emits only the repo header when no product is set', () => {
    expect(buildScopeHeaders(null, 'atlas-platform')).toEqual({
      'X-Forge-Repo-Id': 'atlas-platform',
    });
  });

  it('emits nothing when neither is set', () => {
    expect(buildScopeHeaders(null, null)).toEqual({});
  });

  it('treats empty strings as unset (omits the header)', () => {
    expect(buildScopeHeaders('', '')).toEqual({});
  });
});
