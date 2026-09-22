import { afterEach, describe, expect, it, vi } from 'vitest';
import { type components, createApiClient } from './client';

const responseBody = [
  { id: 'sample', title: 'Sample', slideCount: 3, deck: 'sample', driver: 'show' },
] satisfies components['schemas']['PresentationSummary'][];

describe('generated API client', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('makes a typed call to the presentations endpoint', async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(responseBody), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const { data, error } = await createApiClient('http://localhost').GET('/api/presentations');
    expect(error).toBeUndefined();
    expect(data).toEqual(responseBody);
    if (!data) throw new Error('expected generated response data');
    const typedResponse = data satisfies components['schemas']['PresentationSummary'][];
    expect(typedResponse[0]?.id).toBe('sample');
    expect(fetchMock).toHaveBeenCalledOnce();
    const request = fetchMock.mock.calls[0]?.[0] as Request;
    expect(request.url).toBe('http://localhost/api/presentations');
  });
});
