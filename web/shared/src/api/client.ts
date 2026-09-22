import createClient from "openapi-fetch";
import type { paths } from "./generated";
import {
  clearAuthSession,
  getAccessToken,
  setAuthSession,
  type AuthTokenResponse,
} from "../auth/authStore";

export type { components, paths } from "./generated";

export type ApiClient = ReturnType<typeof createApiClient>;

type FetchInput = RequestInfo | URL;
type FetchInit = RequestInit | undefined;

function withAuthorization(input: FetchInput, init: FetchInit): [FetchInput, RequestInit] {
  const headers = new Headers(input instanceof Request ? input.headers : init?.headers);
  const token = getAccessToken();
  if (token) headers.set("Authorization", `Bearer ${token}`);
  const requestInit = { ...init, headers };
  return input instanceof Request
    ? [new Request(input, requestInit), {}]
    : [input, requestInit];
}

function refreshUrl(input: FetchInput) {
  const raw = input instanceof Request ? input.url : String(input);
  try {
    return new URL("/v1/auth/refresh", raw).toString();
  } catch {
    return "/v1/auth/refresh";
  }
}

async function authenticatedFetch(
  input: FetchInput,
  init?: FetchInit,
): Promise<Response> {
  const [authorizedInput, authorizedInit] = withAuthorization(input, init);
  const response = await fetch(authorizedInput, authorizedInit);
  const requestUrl = input instanceof Request ? input.url : String(input);
  if (response.status !== 401 || requestUrl.endsWith("/v1/auth/refresh"))
    return response;

  const refreshed = await fetch(refreshUrl(input), {
    method: "POST",
    credentials: "include",
    headers: { Accept: "application/json" },
  });
  if (!refreshed.ok) {
    clearAuthSession();
    return response;
  }

  setAuthSession((await refreshed.json()) as AuthTokenResponse);
  const [retryInput, retryInit] = withAuthorization(input, init);
  return fetch(retryInput, retryInit);
}

export function createApiClient(baseUrl?: string) {
  const env = (import.meta as ImportMeta & { env?: Record<string, string | undefined> }).env;
  return createClient<paths>({
    baseUrl: baseUrl ?? env?.VITE_API_URL ?? "",
    fetch: authenticatedFetch,
  });
}

const apiClient = createApiClient();
export default apiClient;
