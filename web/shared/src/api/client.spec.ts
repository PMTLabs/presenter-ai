import { afterEach, describe, expect, it, vi } from 'vitest';
import { type components, createApiClient } from './client';
import { clearAuthSession, setAuthSession } from '../auth/authStore';

const responseBody = [
  { id: 'sample', title: 'Sample', slideCount: 3, deck: 'sample', driver: 'show' },
] satisfies components['schemas']['PresentationSummary'][];

describe('generated API client', () => {
  afterEach(() => {
    clearAuthSession();
    vi.unstubAllGlobals();
  });

  it('adds the in-memory access token as a bearer header', async () => {
    setAuthSession({
      accessToken: 'access-token',
      expiresAt: '2099-01-01T00:00:00Z',
      user: { id: 'usr_test', email: 'test@example.invalid', displayName: null, role: 'user' },
    });
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(responseBody), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    await createApiClient('http://localhost').GET('/api/presentations');
    const request = fetchMock.mock.calls[0]?.[0] as Request;
    expect(request.headers.get('Authorization')).toBe('Bearer access-token');
  });

  it('refreshes once after a 401 and retries with the new token', async () => {
    setAuthSession({
      accessToken: 'expired-token',
      expiresAt: '2099-01-01T00:00:00Z',
      user: { id: 'usr_test', email: 'test@example.invalid', displayName: null, role: 'user' },
    });
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(null, { status: 401 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({
        accessToken: 'fresh-token',
        expiresAt: '2099-01-01T00:00:00Z',
        user: { id: 'usr_test', email: 'test@example.invalid', displayName: null, role: 'user' },
      }), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify(responseBody), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    await createApiClient('http://localhost').GET('/api/presentations');
    expect(fetchMock).toHaveBeenCalledTimes(3);
    const refresh = fetchMock.mock.calls[1]?.[1] as RequestInit;
    expect(refresh.credentials).toBe('include');
    const retry = fetchMock.mock.calls[2]?.[0] as Request;
    expect(retry.headers.get('Authorization')).toBe('Bearer fresh-token');
  });

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
