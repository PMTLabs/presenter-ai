import createClient from "openapi-fetch";
import type { paths } from "./generated";
import { apiBaseUrl } from "./baseUrl";
import {
  clearAuthSession,
  getAccessToken,
  refreshAuth,
} from "../auth/authStore";

export type { components, paths } from "./generated";

export type ApiClient = ReturnType<typeof createApiClient>;

type FetchInput = RequestInfo | URL;
type FetchInit = RequestInit | undefined;

function withAuthorization(input: FetchInput, init: FetchInit, token: string | null): [FetchInput, RequestInit] {
  const headers = new Headers(input instanceof Request ? input.headers : init?.headers);
  if (token) headers.set("Authorization", `Bearer ${token}`);
  const requestInit = { ...init, headers };
  return input instanceof Request
    ? [new Request(input, requestInit), {}]
    : [input, requestInit];
}

function isNonBearerAuthRequest(input: FetchInput): boolean {
  const raw = input instanceof Request ? input.url : String(input);
  try {
    const path = new URL(raw, "http://presenter-ai.invalid").pathname;
    return path.startsWith("/v1/auth/") && path !== "/v1/auth/me";
  } catch {
    return false;
  }
}

async function authenticatedFetch(
  baseUrl: string,
  input: FetchInput,
  init?: FetchInit,
): Promise<Response> {
  const tokenUsed = getAccessToken();
  const retrySource = input instanceof Request ? input.clone() : input;
  const [authorizedInput, authorizedInit] = withAuthorization(input, init, tokenUsed);
  const response = await fetch(authorizedInput, authorizedInit);
  if (response.status !== 401 || isNonBearerAuthRequest(input))
    return response;

  // A concurrent refresh may have replaced the token while this request was in flight. Retrying it is enough;
  // redeeming the cookie again would race rotation.
  if (getAccessToken() !== tokenUsed) {
    const [retryInput, retryInit] = withAuthorization(retrySource, init, getAccessToken());
    return fetch(retryInput, retryInit);
  }

  const refreshed = await refreshAuth(baseUrl);
  if (!refreshed) {
    // Do not let a loser of a concurrent refresh clear the winner's replacement session.
    if (getAccessToken() === tokenUsed)
      clearAuthSession();
    return response;
  }

  const [retryInput, retryInit] = withAuthorization(retrySource, init, getAccessToken());
  return fetch(retryInput, retryInit);
}

export function createApiClient(baseUrl?: string) {
  const resolvedBaseUrl = baseUrl ?? apiBaseUrl();
  return createClient<paths>({
    baseUrl: resolvedBaseUrl,
    fetch: (input: Request) => authenticatedFetch(resolvedBaseUrl, input),
  });
}

const apiClient = createApiClient();
export default apiClient;
