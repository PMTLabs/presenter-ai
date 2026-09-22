import { afterEach, describe, expect, it, vi } from 'vitest';
import { type components, createApiClient } from './client';
import { clearAuthSession, getAccessToken, refreshAuth, setAuthSession, useAuthStore } from '../auth/authStore';

const requestUrl = (input: RequestInfo | URL) => input instanceof Request ? input.url : String(input);

const responseBody = {
  items: [{ id: 'sample', title: 'Sample', slideCount: 3, deck: 'sample', driver: 'show' }],
  page: 1,
  pageSize: 25,
  total: 1,
};

describe('generated API client', () => {
  afterEach(() => {
    clearAuthSession();
    vi.unstubAllGlobals();
    vi.unstubAllEnvs();
  });

  it('adds the in-memory access token as a bearer header', async () => {
    setAuthSession({
      accessToken: 'access-token',
      expiresAt: '2099-01-01T00:00:00Z',
      user: { id: 'usr_test', email: 'test@example.invalid', displayName: null, role: 'user' },
    });
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(responseBody), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    await createApiClient('http://localhost').GET('/v1/presentations');
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

    await createApiClient('http://localhost').GET('/v1/presentations');
    expect(fetchMock).toHaveBeenCalledTimes(3);
    const refresh = fetchMock.mock.calls[1]?.[1] as RequestInit;
    expect(refresh.credentials).toBe('include');
    const retry = fetchMock.mock.calls[2]?.[0] as Request;
    expect(retry.headers.get('Authorization')).toBe('Bearer fresh-token');
  });

  it('uses one refresh flight for simultaneous 401 responses and retries both with the replacement token', async () => {
    setAuthSession({
      accessToken: 'expired-token',
      expiresAt: '2099-01-01T00:00:00Z',
      user: { id: 'usr_test', email: 'test@example.invalid', displayName: null, role: 'user' },
    });
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const request = input as Request;
      if (requestUrl(input).endsWith('/v1/auth/refresh')) {
        return new Response(JSON.stringify({
          accessToken: 'fresh-token', expiresAt: '2099-01-01T00:00:00Z',
          user: { id: 'usr_test', email: 'test@example.invalid', displayName: null, role: 'user' },
        }), { status: 200 });
      }
      return request.headers.get('Authorization') === 'Bearer fresh-token'
        ? new Response(JSON.stringify(responseBody), { status: 200 })
        : new Response(null, { status: 401 });
    });
    vi.stubGlobal('fetch', fetchMock);

    await Promise.all([
      createApiClient('http://localhost').GET('/v1/presentations'),
      createApiClient('http://localhost').GET('/v1/presentations'),
    ]);

    expect(fetchMock.mock.calls.filter(([input]) => requestUrl(input as RequestInfo | URL).endsWith('/v1/auth/refresh'))).toHaveLength(1);
    const retries = fetchMock.mock.calls
      .map(([input]) => input)
      .filter((input): input is Request => input instanceof Request)
      .filter((request) => request.headers.get('Authorization') === 'Bearer fresh-token');
    expect(retries).toHaveLength(2);
  });

  it('shares the startup refresh flight with a protected request 401', async () => {
    setAuthSession({
      accessToken: 'expired-token',
      expiresAt: '2099-01-01T00:00:00Z',
      user: { id: 'usr_test', email: 'test@example.invalid', displayName: null, role: 'user' },
    });
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const request = input as Request;
      if (requestUrl(input).endsWith('/v1/auth/refresh')) {
        return new Response(JSON.stringify({
          accessToken: 'fresh-token', expiresAt: '2099-01-01T00:00:00Z',
          user: { id: 'usr_test', email: 'test@example.invalid', displayName: null, role: 'user' },
        }), { status: 200 });
      }
      return request.headers.get('Authorization') === 'Bearer fresh-token'
        ? new Response(JSON.stringify(responseBody), { status: 200 })
        : new Response(null, { status: 401 });
    });
    vi.stubGlobal('fetch', fetchMock);

    await Promise.all([refreshAuth('http://localhost'), createApiClient('http://localhost').GET('/v1/presentations')]);

    expect(fetchMock.mock.calls.filter(([input]) => requestUrl(input as RequestInfo | URL).endsWith('/v1/auth/refresh'))).toHaveLength(1);
  });

  it('does not let a failed refresh clear a session replaced while it was in flight', async () => {
    setAuthSession({
      accessToken: 'expired-token',
      expiresAt: '2099-01-01T00:00:00Z',
      user: { id: 'usr_test', email: 'test@example.invalid', displayName: null, role: 'user' },
    });
    let resolveRefresh: ((response: Response) => void) | undefined;
    let signalRefreshStarted: (() => void) | undefined;
    const refreshStarted = new Promise<void>((resolve) => { signalRefreshStarted = resolve; });
    const fetchMock = vi.fn((input: RequestInfo | URL) => {
      if (requestUrl(input).endsWith('/v1/auth/refresh')) {
        signalRefreshStarted?.();
        return new Promise<Response>((resolve) => { resolveRefresh = resolve; });
      }
      return Promise.resolve(new Response(null, { status: 401 }));
    });
    vi.stubGlobal('fetch', fetchMock);

    const request = createApiClient('http://localhost').GET('/v1/presentations');
    await refreshStarted;
    setAuthSession({
      accessToken: 'newer-token',
      expiresAt: '2099-01-01T00:00:00Z',
      user: { id: 'usr_new', email: 'new@example.invalid', displayName: null, role: 'user' },
    });
    resolveRefresh!(new Response(null, { status: 401 }));
    await request;

    expect(getAccessToken()).toBe('newer-token');
    expect(useAuthStore.getState().user?.id).toBe('usr_new');
  });

  it('uses the configured API base for refresh, development sign-in, and logout', async () => {
    vi.stubEnv('VITE_API_URL', 'https://api.example.test');
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({
      accessToken: 'fresh-token', expiresAt: '2099-01-01T00:00:00Z',
      user: { id: 'usr_test', email: 'test@example.invalid', displayName: null, role: 'user' },
    }), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    await refreshAuth();
    await useAuthStore.getState().signInDev();
    await useAuthStore.getState().signOut();

    expect(fetchMock.mock.calls.map(([input]) => input)).toEqual([
      'https://api.example.test/v1/auth/refresh',
      'https://api.example.test/v1/auth/dev/sign-in',
      'https://api.example.test/v1/auth/logout',
    ]);
  });

  it('retries a consumed JSON request body with the fresh token', async () => {
    setAuthSession({
      accessToken: 'expired-token',
      expiresAt: '2099-01-01T00:00:00Z',
      user: { id: 'usr_test', email: 'test@example.invalid', displayName: null, role: 'user' },
    });
    const bodies: string[] = [];
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      if (requestUrl(input).endsWith('/v1/auth/refresh')) {
        return new Response(JSON.stringify({
          accessToken: 'fresh-token', expiresAt: '2099-01-01T00:00:00Z',
          user: { id: 'usr_test', email: 'test@example.invalid', displayName: null, role: 'user' },
        }), { status: 200 });
      }

      const request = input as Request;
      bodies.push(await request.text());
      return request.headers.get('Authorization') === 'Bearer fresh-token'
        ? new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } })
        : new Response(null, { status: 401 });
    });
    vi.stubGlobal('fetch', fetchMock);

    await createApiClient('http://localhost').POST('/v1/auth/sso/token', {
      body: { code: 'code', codeVerifier: 'verifier' },
    });

    expect(bodies).toEqual([
      '{"code":"code","codeVerifier":"verifier"}',
      '{"code":"code","codeVerifier":"verifier"}',
    ]);
    const retry = fetchMock.mock.calls[2]?.[0] as Request;
    expect(retry.headers.get('Authorization')).toBe('Bearer fresh-token');
  });

  it('makes a typed call to the presentations endpoint', async () => {
    const fetchMock = vi.fn(async () => new Response(JSON.stringify(responseBody), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    }));
    vi.stubGlobal('fetch', fetchMock);

    const { data, error } = await createApiClient('http://localhost').GET('/v1/presentations');
    expect(error).toBeUndefined();
    expect(data).toEqual(responseBody);
    if (!data) throw new Error('expected generated response data');
    const typedResponse = data satisfies components['schemas']['ListResponseOfPresentationSummary'];
    expect(typedResponse.items[0]?.id).toBe('sample');
    expect(fetchMock).toHaveBeenCalledOnce();
    const request = fetchMock.mock.calls[0]?.[0] as Request;
    expect(request.url).toBe('http://localhost/v1/presentations');
  });
});
